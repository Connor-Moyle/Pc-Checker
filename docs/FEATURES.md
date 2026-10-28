# Features

A page-by-page tour of what PC Checker does, what every button does, and
where each page stops. Sections marked **Advanced** only appear when
*Advanced mode* is on (Settings → General, or the toggle at the bottom of the
navigation pane).

The side menu has one item per section. A section with several pages shows
them as tabs above the content:

```
Home            Dashboard · Check-up
Monitor         Temperatures · Power · History
Task manager    Processes · Live usage · App history · Services · Users
Hardware        Overview · Health · Storage · Memory · Battery
Devices         Displays · Audio · Keyboard & Mouse
Cooling & RGB   Fans · Lighting
Apps & Drivers  Apps · Leftovers · Drivers · Old drivers · Windows Update ·
                Startup · Scheduled tasks · Windows Features
Clean up        Cleanup · Disk space · Debloat · Right-click menu · Registry
Security        Security · Privacy
Performance     Speed check · Optimize · Benchmark · Overclock
Gaming          Overlay · Latency · Missing DLLs
Network         Network · Connections · Hosts file
Ask Claude      Ask Claude
Footer          Logs · Settings
```

Every page that changes something follows the same pattern: a confirmation
dialog that explains the change, a restore point (per Settings), an entry in
the Action Journal with an undo hint, and a log line. See
[SAFETY.md](SAFETY.md).

---

## Home

### Dashboard

The first thing you see.

- **What needs attention** at the top: up to five recommendations, each with
  one button (see [Help, shortcuts and undo](#help-shortcuts-and-undo)).
- **Health score** gauge from the last health check, or a *Run a health check*
  button if there is none yet.
- **Live tiles**: CPU temperature, GPU temperature, memory used and current
  power draw, straight from the sensor service.
- **Check-up strip**: what the last check-up found (or what a check-up does)
  with *Run check-up*, which opens the Check-up page and starts it.
- **At a glance**: *Windows Update* (service turned off, restart pending,
  updates waiting from the last check-up, paused, or up to date, with when it
  was last checked), *Temperatures, last 24 hours* (CPU and GPU sparklines
  from History, half-hour peaks, with the day's peak; a note instead when
  History is off or has nothing yet) and *Fullest drive* (the drive with the
  largest used share from Disk space, with a bar and the free space; the
  hardware scan is the fallback). Each links to its page.
- **Alerts strip**: temperature alerts, critical health items, a pending
  reboot, an update that is ready, a fan or overclock watchdog notice from the
  last start.
- **Quick actions**: *Full scan* (runs Hardware → Health → Performance →
  Software → Drivers with progress), *Clean temp files*, *Check drivers*,
  *Optimize* (each opens its page) and *Export full report* (HTML, JSON or
  Markdown through a save dialog).
- **Controls row**: the active fan profile, lighting connection status, how
  many displays and the active mode, and the default audio device. Each tile
  tolerates an unavailable service and says so.
- **Hardware support**: your GPU and CPU vendor and what the app can control
  on them, from the Overclock page's detection.
- **Recent activity**: the last five Action Journal entries.
- **System summary**: CPU, GPU, RAM, storage, OS build and uptime.

### Check-up

One button checks everything and turns the results into one to-do list.
Nothing changes until you choose a fix.

- **Run check-up** runs, one after another with one combined progress bar
  and a Cancel button: the health check, a cleanup scan of the Safe
  categories (the ones you picked on the Cleanup page, or the defaults), the
  installed-driver scan and the driver update check, the Windows Update
  status and update check, the startup apps with their estimated impact, the
  speed check, the state of the Debloat page's *Recommended* settings and the
  old-driver scan. It uses the same services as those pages; a part that
  fails or takes more than five minutes is skipped and named in a warning.
- **The to-do list** has one line per thing worth doing: the title, why it
  matters, the benefit of fixing it, its risk (*Safe*, *Moderate* or *Advanced*)
  and the page it comes from (a link). Serious problems come first; within
  the same level, things the app can fix by itself, safer fixes and bigger
  gains come first. Repeats are merged (a restart pending reported by both
  Health and Windows Update is listed once).
- **Fix** is offered where the owning service has a safe automatic fix:
  clean the Safe junk, stop a heavy startup app starting with Windows,
  install the recommended Windows updates or the offered driver updates,
  apply a reversible optimization behind a speed check finding, apply the
  Safe *Recommended* privacy settings that are not applied yet, and remove
  old driver packages marked *Safe to remove*. Everything else has
  **Open**, which takes you to the page.
- **Selection**: Safe fixes that suit everyone are ticked after a check-up.
  Startup apps, the privacy settings and the speed check optimizations
  (such as turning on Storage Sense for a full drive, which also empties the
  Recycle Bin over time) are choices for you and are never ticked for you,
  Moderate fixes (driver updates) are never ticked automatically, and
  Advanced ones can only be ticked in Advanced mode.
- **Fix selected** confirms what will happen, naming the setting an
  optimization changes rather than only the finding it helps with, then
  applies the ticked fixes
  one service at a time: junk, old drivers, startup apps, optimizations,
  recommended settings, driver updates, Windows updates. Each service runs
  its change through the change guard as on its own page, but the run
  shares **one restore point** created first, so a batch does not make one
  restore point per page (and Windows does not refuse the second one). If
  that restore point cannot be created, every change follows its own rules
  and the ones that need a restore point do not run. Each item shows its
  result; the Action Journal lists every change. Cancel stops before the
  next step; driver installs and removals cannot be stopped once started, so
  Cancel is hidden for a run that includes them.
- In demo mode the page runs a check-up on its first visit; in normal mode it
  waits for the button and keeps the last result for the session.

Limitations: the check-up always scans afresh, so it takes a few minutes
(the Windows and driver update checks can take a minute each). The Health and
Speed check pages show its new results; the other pages keep their own scans.

---

## Monitor

### Temperatures

Live readings from every sensor LibreHardwareMonitor can see.

- **Gauges** for CPU, GPU, the hottest drive and the motherboard or VRM,
  coloured by your warning and critical thresholds.
- One expander per piece of hardware (CPU, GPU, each drive, motherboard,
  memory, PSU or cooler) listing each temperature sensor with its current,
  minimum, maximum and average for the session and a sparkline.
- **Drives**: the drive temperature is the NVMe composite (or SMART 194 on
  SATA). Extra NVMe sensors are listed as *Sensor N* or *Controller*, marked
  "extra sensor", with limits 15 °C above the drive limits. The warning and
  critical limits a drive reports are not shown as temperatures.
- **Pause / Resume**, **Reset min/max**, and **Thresholds**, an inline editor
  for the warning and critical temperatures per component (the same values
  are in Settings → Monitoring).
- **Alerts**: the last twenty threshold crossings. Alerts re-fire only after
  the temperature drops 3 °C below the threshold or five minutes pass.
- **Advanced**: an *All sensors* table with every sensor kind: loads, clocks,
  fan speeds, voltages, power, data counters.

Limitations: the sensor driver needs administrator rights, CPU and motherboard
sensors need the PawnIO driver installed, and another monitoring tool can hold
the hardware; the
page shows the exact reason and a *Retry* button. Laptops and OEM desktops
often expose very few sensors, and virtual machines almost none.

### Power

- **Tiles**: total draw now, CPU package, GPU, battery discharge, energy used
  this session (Wh), estimated cost per hour and per day at the price in
  Settings.
- A 30-minute sparkline of total watts and a per-component table.
- **Battery**: charge, on-battery state, discharge rate and an estimated time
  remaining.
- **Power plan**: pick a plan and *Apply* (confirmed, journaled, reversible).
- **Electricity price** editor.

Limitations: the total is the sum of known sensors plus a baseline when there
is no PSU sensor; the page labels it *Estimated* in that case. A PC with no
CPU or GPU power sensors shows dashes.

### History

What the PC did over the last hour, day, week, month or three months.

- **Recording**: while PC Checker is open, also when it is minimised to the
  tray, a background recorder takes one sample a minute (configurable): the
  highest CPU and GPU temperature in that minute, the average CPU and GPU
  load, power draw and memory use, and the free space of every fixed drive
  once an hour. Every health check (on the Health page, the Dashboard or a
  scheduled run) adds its score.
- **Storage**: one small JSON Lines file per day in
  `%LOCALAPPDATA%\PcChecker\history`. After 7 days a day is rolled up to
  15-minute points and after 30 days to hourly points (temperature peaks and
  the lowest health score are kept, loads and power are averaged), so 90 days
  take a few megabytes. Days older than the retention setting (default 90)
  are deleted. A line cut short by a crash or power cut is skipped and
  dropped at the next roll-up. Nothing leaves the PC.
- **Range picker**: last hour, 24 hours, 7 days, 30 days or 90 days.
- **Tiles**: hottest CPU and GPU with when it happened, energy used in kWh
  with its cost at your electricity price, and the number of notable events.
- **Charts**: one per metric (temperatures with the warning limit as a dashed
  line, loads, power, memory, health score, free space per drive) with value
  and time axes, average, highest and lowest, gaps where the PC was off, and
  the exact value under the mouse.
- **Notable events**: temperature spells above your warning limits (one per
  spell, at its peak; critical when the critical limit was reached), sudden
  drops in a drive's free space (at least 10 GB and 5 % within a day), and
  health scores that fell by 5 points or more.
- **Export CSV** saves the recorded values of the chosen range. *More* opens
  the history folder or the recording settings.
- **Settings → History**: record on or off, how often (30 seconds to 15
  minutes) and how many days to keep (7 to 365).

Limitations: nothing is recorded while PC Checker is closed, so those times
show as gaps. Temperatures, loads and power need the same sensors as the
Temperatures and Power pages; without them only memory, free space and
health scores are recorded. Demo mode shows 30 days of generated history and
writes nothing to disk.

### Crashes

Blue screens, sudden shutdowns, graphics driver resets and app crashes from
the last 90 days, explained in plain words. The page only reads; it never
changes or deletes anything.

- **Where it looks**: the System log (BugCheck event 1001, Kernel-Power 41,
  EventLog 6008, Display 4101), the Application log (Application Error 1000,
  Application Hang 1002), the small memory dumps in `%SystemRoot%\Minidump`,
  `MEMORY.DMP` and the graphics driver reports in
  `%SystemRoot%\LiveKernelReports\WATCHDOG`. Values come from the event data
  and the dump headers, never from the translated message, so it works on
  every Windows language.
- **One entry per incident**: a dump is matched to its BugCheck event, a
  Kernel-Power 41 that repeats a blue screen and an EventLog 6008 next to it
  are folded in, and dumps whose event was cleared get their own entry. When
  Windows wrote both `MEMORY.DMP` and a small dump for the same crash, they
  make one entry. A blue screen shows at the crash time from the dump, not
  when Windows logged it at the next start.
- **Tiles**: blue screens, sudden shutdowns, driver resets and app crashes in
  the last 30 days.
- **Timeline**: newest first, grouped by day, with a filter by type and date
  range (7, 30 or 90 days). Right-click a crash to copy its details, ask
  Claude, search online, open Event Viewer or the dump folder; Ctrl+C copies
  the selected crash.
- **Details**: what the stop code or error means, the likely cause, first
  steps (safest first), buttons to the right PC Checker page (Drivers, Memory
  test, Storage, Health, Temperatures, and Overclock when PC Checker has an
  overclock applied and the stop code points at hardware), Microsoft's bug
  check reference for the code, and *Ask Claude about this crash*, which opens
  Ask Claude with the details filled in.
- **Probably caused by**: read from small memory dumps. A driver whose memory
  a stop-code parameter or the crash address points into, else the first
  driver from outside Windows on the crashing thread's stack, else the
  graphics driver for a graphics stop code. It is labelled as a best guess.
- **Stop codes explained**: about 50 common ones, such as
  IRQL_NOT_LESS_OR_EQUAL, SYSTEM_SERVICE_EXCEPTION, PAGE_FAULT_IN_NONPAGED_AREA,
  DPC_WATCHDOG_VIOLATION, CLOCK_WATCHDOG_TIMEOUT, WHEA_UNCORRECTABLE_ERROR,
  VIDEO_TDR_FAILURE, MEMORY_MANAGEMENT and CRITICAL_PROCESS_DIED. App crashes
  explain their exception code (access violation, stack buffer overrun, heap
  corruption, .NET exception, illegal instruction and more).
- **Most crashing apps**: programs by number of crashes and hangs, with the
  file they crash in most often; *Show these* filters the timeline.
- **Hardware errors**: WHEA reports are explained on the Health page; this
  page shows how many there were and links there.
- **Dashboard**: *What needs attention* mentions blue screens and sudden
  shutdowns from the last 7 days.

Limitations: the guess uses no debugging symbols and does not walk the stack,
so it can be wrong; WinDbg gives the definitive answer. Complete and kernel
dumps (`MEMORY.DMP`) only give the stop code and parameters, not the driver
list; the guess then comes from the small dump of the same crash when there is
one. Overclocks set in the BIOS or other tools cannot be seen. Demo mode
shows two blue screens, a sudden shutdown, three driver resets and crashes of
two games.

---

## Task manager

### Processes

*Task manager → Processes.* Everything Windows 11 Task Manager's Processes
and Details tabs do, in plain words. The list refreshes while the page is
open at the chosen **Update speed**: High (twice a second), Normal (every
second), Low (every 4 seconds) or Paused; *Pause updates* freezes it to read.

- **Tiles**: total CPU use, memory in use, the number of processes (apps and
  background programs), and the busiest program right now. A banner names any
  program that is not responding.
- **Processes view** (default): three sections like Task Manager. *Apps* are
  programs with a window on the taskbar; the helpers they started (Steam's web
  helpers, Chrome's tabs) are listed under them with summed figures, and a
  game started from Steam is an app of its own. *Background processes* run
  without a window; *Windows processes* are part of Windows (including
  Microsoft-signed programs in the Windows folder; drivers' helpers count as
  background). Expand a group with its arrow, a double-click or the Right
  arrow key. Under a browser, helpers are labelled *Tab or extension*,
  *Graphics helper*, *Network helper* and so on.
- **Details view** (switch): one flat list of every process with its file
  name, like Task Manager's Details tab.
- **Columns**: Name (icon, friendly name, window title or *what is this?*),
  Status (*Not responding*, *Suspended*, *Efficiency mode*), PID, CPU, Memory
  (private working set, the figure Task Manager shows), Disk (MB/s), Disk read
  and write, Network, GPU (the busiest graphics engine, as Task Manager
  shows it), GPU engine, Power usage and Power usage trend (Very low to Very
  high, estimated from processor, graphics and disk use; the trend covers two
  minutes), Publisher with its signature state, User name, Session,
  Architecture (x64, x86, ARM64), Elevated, Priority, Handles, Threads,
  Started, Description, Command line and File. *Columns* (or a right-click on
  a column title) picks which ones show; the choice is saved separately for
  each view, and *Reset to default columns* brings back the defaults. Busy
  cells get a stronger orange tint (heat map) that keeps the text readable in
  both themes. Click a title to sort; drag a title to move the column.
- **Search** (Ctrl+F): name, description, window title, PID, publisher or
  folder.
- ***What is this?***: a built-in catalogue explains more than 120 well-known
  Windows, Defender, driver (NVIDIA, AMD, Intel, Realtek), launcher (Steam,
  Epic, Battle.net, EA, Ubisoft), browser, chat and hardware-utility
  processes. Unknown programs show their signed publisher instead.
- **Publisher**: each program file gets an offline Authenticode check (its
  own signature, then the Windows catalogs) once per session.
- **Flags** (information only): a warning sign marks programs that run from a
  Temp or AppData folder without a valid signature, unknown and unsigned
  programs using a lot of CPU, GPU or memory, and programs with a Windows
  name running from outside the Windows folder. *Only flagged* filters the
  list. PC Checker never acts on a flag by itself.
- **Actions** (header, *More*, right-click and the selected program's card),
  each confirmed first:
  - *End task* (Delete): a program with a window is asked to close, as if
    you clicked its X, so it can save; if it is still open after a few
    seconds you choose *End now* or *Switch to it*. A program that is not
    responding, suspended or has no window is ended straight away. For a
    group, every process of the program.
  - *End process tree*, *Restart* (closes the program and starts the same
    file with the same options, with your normal rights unless it had
    administrator rights and lives in Program Files or the Windows folder;
    File Explorer is restarted by Windows itself). Store apps, Windows'
    own programs and browser helper processes are not restarted: start them
    again from the Start menu or restart the main program.
  - *Suspend* / *Resume*: pauses a program so it uses no processor time.
    Store and Windows apps that Windows paused itself (Search, Start) are
    left alone; Windows wakes them when you open them.
  - *Efficiency mode*: Windows' EcoQoS power throttling plus Low priority,
    as Task Manager sets it; turning it off restores Normal priority.
  - *Set priority* (Realtime only in Advanced mode, with a warning) and *Set
    affinity* (one tick per logical processor).
  - *Switch to* (Enter or double-click), *Minimize*, *Maximize*.
  - *Create memory dump file*: a full dump in your Temp folder
    (`name.DMP`, then `name (2).DMP`), opened in File Explorer afterwards;
    the dialog warns that it can contain anything that was open.
  - *Open file location*, *Search online*, *Properties* (Windows' file
    properties) and *Copy details* (Ctrl+C).
- **Run new task** (header): opens a program, folder, document or web
  address and remembers the last ten. It starts with your normal rights,
  even though PC Checker runs as administrator, unless you tick *Create this
  task with administrative privileges*. Only programs in Program Files or
  the Windows folder are started as administrator (a bare name such as
  `cmd` is looked up on the PATH first). The program opens through the
  desktop, so a mistyped name shows Windows' own error message.

Every change is recorded in the Action Journal with an undo hint; no restore
point is made because no Windows setting changes.

Never offered: ending, pausing, throttling or re-prioritising System,
Registry, Memory Compression, smss, csrss, wininit, winlogon, services, lsass
and PC Checker itself (Windows' own "critical" flag is checked too).
Windows processes are never suspended or put in efficiency mode, services and
other accounts' programs are not restarted, and no memory dump is made of
Local Security Authority (it holds sign-in secrets).

Limitations: Windows does not report network data per program without an
event trace, so the Network column counts open connections (TCP and UDP)
instead of showing Mbps. Power usage is an estimate, like Task Manager's.
Protected processes and programs of other accounts may refuse an action or
show no path, user, command line or memory figure. The GPU columns are empty
when the graphics driver has no *GPU Engine* performance counters. Affinity
is offered on PCs with up to 64 logical processors. Starting a program
without administrator rights needs File Explorer's desktop to be running.
CPU and disk figures are averages over the last refresh.

### Live usage

Task manager → Live usage is the equivalent of Task Manager's Performance tab.
It reads once a second while the page or the compact window is showing and
stops when neither is (**Pause / Resume** in the header).

- **List** (left): CPU, Memory, every physical disk (*Disk 0 (C: D:)* with
  SSD, HDD, SSD (NVMe) or Removable), every active network adapter (Ethernet,
  Wi-Fi with the network name) and every graphics card, each with a
  sparkline of the last minute and its current value. Arrow keys move
  between them.
- **CPU**: utilization graph, or one small graph per logical processor
  (right-click or *More* → *Processor graph*); *Show kernel times* shades the
  time spent inside Windows. Details: utilization, current speed (from the
  processor's performance counter times its base speed), processes, threads,
  handles, up time, base speed, sockets, cores, logical processors,
  virtualization and the L1, L2 and L3 cache sizes.
- **Memory**: usage graph and a composition bar (in use, modified, standby,
  free). Details: in use with the compressed amount, available, committed
  out of the limit, cached, paged and non-paged pool, speed, slots used,
  form factor, type and hardware reserved.
- **Disk**: active time graph and a transfer-rate graph (reads solid, writes
  dashed). Details: active time, average response time, read and write
  speed, capacity, formatted space, whether it holds Windows or a page file,
  type and model.
- **Network**: received (solid) and sent (dashed) on a scale that grows in
  readable steps. Details: send, receive, Wi-Fi signal and network name,
  adapter name, connection type (802.11ax, Ethernet), link speed, IPv4 and
  IPv6 addresses.
- **GPU**: graphs for the 3D, Copy, Video Encode and Video Decode engines
  (or the busiest others the card has), dedicated and shared memory graphs.
  Details: utilization (the busiest engine, as Task Manager counts it),
  dedicated, shared and total GPU memory, temperature when the Temperatures
  sensors are running, driver version and date, DirectX version with the
  feature level, and the physical location (PCI bus).
- **Copy details** (Ctrl+C, *More* or right-click) puts the selected
  resource's details on the clipboard as text.
- **Compact view**: a small always-on-top window with every sparkline (the
  header button, *More* or the tray menu's *Compact live usage*). Drag it
  anywhere; it remembers where it was. Double-click it to come back to the
  page; Esc closes it.

How it reads: performance counters added by their English names, so they
work on every Windows language (Processor Information, Memory, PhysicalDisk,
GPU Engine and GPU Adapter Memory), `GetPerformanceInfo` for counts and the
commit charge, the adapters' own byte counters for network throughput,
`netsh wlan` for the Wi-Fi name and signal, DXGI and WMI for the graphics
cards, and WMI for disks, memory modules and slots. Nothing is changed.

Limitations: when Windows' performance counters are damaged the graphs show
0 and the page says so (`lodctr /R` as administrator repairs them). GPU
graphs need a WDDM 2 driver with GPU Engine counters. The compressed amount
needs the Memory Compression process to be readable and shows nothing
otherwise. On a PC with more than 64 logical processors, the core count and
cache sizes cover only the first processor group.

### App history

Task manager → App history is the equivalent of Task Manager's App history
tab: how much each app used the processor, the network and the disk over a
date range (**Today**, **Last 7 days**, **Last 30 days** or **Since a date**).

- **Tiles**: the app with the most processor time, the app with the most
  network data, the total data used by every app (with the metered part) and
  how many apps were used.
- **List**: name with icon and publisher, CPU time, network (sent and
  received), metered network, disk read, disk written, first and last seen.
  Click a column to sort; search by name or path. Right-click (or the buttons
  below the list) for **Open file location** (Enter) and **Go to Processes**,
  which looks the app up among the running programs.
- **Source**: *Best available* (the default) shows Windows' own records when
  PC Checker can read them and PC Checker's own recording otherwise; you can
  pick either one. The page always says which one is shown and, when Windows'
  records could not be read, why.
- **Delete usage history** (*More*, confirmed, recorded in the Action
  Journal) deletes PC Checker's own recording. Windows' records can only be
  cleared by Windows; *More* → *Windows activity settings* opens Settings →
  Privacy & security → Activity history.

**Windows' records** come from the System Resource Usage Monitor (SRUM), the
same data Task Manager uses. Windows keeps them in
`C:\Windows\System32\sru\SRUDB.dat`, an ESE database the Diagnostic Policy
Service holds open, so PC Checker never reads it in place: the built-in
`esentutl /y … /vssrec` copies it from a Volume Shadow Copy snapshot and
replays its logs so the copy is clean (if that fails, a plain `/vss` copy is
made and `esentutl /p` repairs the copy), into a new folder under ProgramData
that only Administrators and SYSTEM can open. The copy is
opened read-only with recovery off through esent.dll (ManagedEsent), the
application resource usage table, the network data usage table and the id map
(`SruDbIdMapTable`: programs as UTF-16 paths, Store apps and services as
names, accounts as binary SIDs) are read, and the folder is deleted straight
away. The result is kept for ten minutes, so changing the range does not copy
again; **Refresh** (F5) copies afresh. Reading needs administrator rights and
takes a few seconds, with progress and Cancel.

How the figures are worked out: SRUM counts processor *cycles* on all cores
together; PC Checker divides them by the processor's base speed (the
registry's `~MHz` value, 3 GHz when it is missing), as Task Manager does. Turbo,
power saving and cores of different speeds make real clocks differ from the
base speed, so CPU time is a close estimate rather than an exact count.
Timestamps are OLE Automation dates in UTC. Foreground and background use are
added together. *Metered* counts data over mobile broadband adapters (the
interface type inside the adapter's LUID); Wi-Fi and Ethernet networks you
set as metered are not told apart, because SRUM does not record that per row.

**PC Checker's own recording** is the fallback for PCs where Windows' records
are missing (the Diagnostic Policy Service is off or a policy turns SRUM off),
locked, damaged or unreadable. While PC Checker runs (also from the tray) and
*History recording* is on (Settings → History), it reads every process's
processor time and disk bytes once a minute and adds up the difference per
program; every ten minutes the totals go to
`%LOCALAPPDATA%\PcChecker\history\apps`, one file per day. Days before today
are rolled up to one line per app per hour, and the History retention setting
deletes older days. A program that was already running when recording
started only counts from then on, and one that ends between two readings loses
its last minute at most. It cannot see network use per app, so those columns
show a dash.

Limitations: Windows writes SRUM about once an hour, so the last hour may be
missing from Windows' records. SRUM keeps about 30 to 60 days. Reading it
needs administrator rights and the Volume Shadow Copy service; without them
the page shows PC Checker's own recording and says why. Apps are added up
over every account on the PC.

### Services

Every Windows service, with what it does and the safe actions a Task Manager
user expects. The list is read through WMI (`Win32_Service`: structured and
the same on non-English Windows), and changes go through the Service Control
Manager APIs — never by parsing `sc.exe` text.

- **Tiles**: how many services there are, how many are running, how many are
  stopped, and how many come from other companies rather than Windows.
- **Columns**: display name with the service name underneath and a lock icon
  on core services, status (Running, Stopped, Paused…), startup type
  (Automatic, Automatic (Delayed), Manual, Disabled), log-on account, PID,
  group and whether it is a Windows or other-company service. The description
  is in the tooltip and the selected-service card.
- **Search and filters**: search the display name, service name, description
  or PID; filter to Running, Stopped, Other companies only or Disabled.
- **Actions** (right-click or the selected-service card): Start, Stop,
  Restart, Pause and Continue where the service allows it; change the startup
  type; open the Services console (`services.msc`); open the service's file;
  search the name online; **Go to the process** (opens the Processes tab with
  that process selected); and **Show dependencies** (what the service depends
  on and what depends on it). Actions a service does not allow are greyed out.
  A Restart stops the running services that depend on it in order, waits for
  each to stop, restarts the service and then starts them again in reverse;
  services that were already stopped are left alone, and the restart is
  refused when it would stop a locked service. Failures are explained in plain
  words (for example "Other running services depend on this one").
- **Safety**: a built-in list of core services (Remote Procedure Call, DCOM,
  Local Session Manager, WMI, Event Log, Plug and Play, Power, the Base
  Filtering Engine, the firewall, Defender and more) can never be stopped or
  set to Manual or Disabled, each with a one-line reason. Windows Update is
  locked the same way when an organization manages it by policy (WSUS or
  update rules); otherwise it and other protection-related services warn
  before being stopped or disabled. A known
  background service (updaters and peripheral helpers) gets a "safe to set to
  Manual" note. Every action is confirmed and journaled through the change
  guard. A startup-type change records the previous type in the Action
  Journal, so it can be set back from the Services page; start, stop and
  restart last until the next restart of Windows.

Limitations: the per-service group comes from the service's registry key and
the dependency lists from the Service Control Manager, so either can be empty
when a service does not record them. Setting Boot or System start types is
not offered; those are driver load types.

### Users

Everyone signed in to this PC, read with the Remote Desktop Services (WTS)
APIs, with the totals of what each person is running.

- **Tile**: how many sessions are signed in.
- **Columns**: user (with a *You* badge on your own session), session id,
  state (Active, Connected, Disconnected…), that user's summed processor use
  and memory, how many programs they are running, and when they signed in.
- **Actions**: **Switch user** locks the PC and shows the sign-in screen so
  someone else can sign in while your session stays open; **Disconnect** locks
  another user's desktop but leaves their programs running; **Sign out**
  closes another user's programs (confirmed first, needs administrator
  rights); and **Send a message** shows a short message box on that user's
  screen. To sign yourself out, PC Checker points you at Switch user or the
  Start menu so you are not cut off part-way through something.

Limitations: per-user processor use is measured over a short sample, so it is
an estimate. The sign-in time is the one Windows records for the session;
only when Windows does not report one is it estimated from the user's earliest
process.

### Replace Task Manager

An option that makes PC Checker open in place of Windows Task Manager, exactly
the way Sysinternals Process Explorer does it: through the Image File Execution
Options *Debugger* value for `taskmgr.exe`. When Windows launches Task Manager
(Ctrl+Shift+Esc, the taskbar menu, anywhere) it launches PC Checker with a
`--taskmgr` switch instead, and PC Checker opens straight on the Processes tab.

- **Where**: Settings → General, and the Processes page *More* menu.
- **Startup apps**: Task Manager's Startup apps tab is Apps & Drivers ›
  Startup; *Startup apps* in the Processes *More* menu opens it.
- **Only when installed safely**: the option is only available when PC Checker
  runs from Program Files and neither the program nor its folder can be
  changed by standard users. A user-writable copy could let another program
  take Task Manager's elevated slot, so the option stays off and explains why.
  **Settings → General → Install in Program Files** (shown only while PC
  Checker runs from somewhere else) copies the running exe to
  `C:\Program Files\PC Checker\PcChecker.exe` in a folder it creates with
  Program Files' own permissions, adds an all-users Start menu shortcut and
  reopens from there. Settings are kept; the original copy is left alone.
- **Safe and reversible**: turning it on and off is confirmed and journaled.
  When another program (such as Process Explorer) is already set in Task
  Manager's place, the confirmation names it; its value is saved and put back
  when the option is turned off.
- **Self-healing**: at every start PC Checker checks the value still points at
  this copy. A leftover setting pointing at a PC Checker that no longer exists
  (after an uninstall or move) is cleaned up automatically; one pointing at
  another copy that still exists is never changed silently: PC Checker offers
  to open this copy instead (also from the Settings row).
- **Opening the real Task Manager**: while the option is on, Windows starts PC
  Checker for every launch of Task Manager and there is no public way around
  that, so Windows Task Manager only opens while the option is off. *Open
  Windows Task Manager* on the Processes *More* menu explains this and turns
  the option off and opens Task Manager in one click.
- **Already open**: Ctrl+Shift+Esc with PC Checker already running brings the
  open window to the front on the Processes tab.

Limitations: PC Checker asks for administrator rights, so on accounts without
them Windows shows a sign-in prompt instead of Task Manager while the option is
on.

---

## Hardware

### Hardware

A complete inventory of the PC, scanned automatically on first visit.

- **Categories** on the left with counts: System, Processor, Graphics,
  Memory, Storage, Network, Displays, Audio, USB, Battery, Input and All
  devices.
- Each item is a card with the typed, human-labelled fields first ("Cores: 8
  (16 threads)", "VRAM: 12 GB", "Secure Boot: on", "TPM 2.0") and an *All
  properties (raw)* list underneath with a search box, so nothing Windows
  knows is hidden.
- **Scan**, **Export JSON** (save dialog) and **Copy summary**.
- A summary strip: CPU, GPU, RAM total and slots, storage total, OS build.
- A warning bar if a WMI class could not be read; the rest still loads.

### Health

Runs each analyzer independently and combines the result into one score
(start at 100; Critical −25, Warning −8).

- **Issues found**: every item that is not *Good*, ordered by severity, with a
  recommendation and an *Explain with Claude* button when the assistant is
  configured.
- **Drives**: SMART status, temperature, wear, power-on hours, reallocated and
  pending sectors, host writes, with a plain-English verdict per drive.
  **Advanced** shows the full SMART attribute table.
- **Battery**: design versus full-charge capacity, wear, cycle count.
- **Device problems**: devices Windows has flagged, the problem code
  explained, what to try, and *Open Device Manager*.
- **Recent system errors**: Critical and Error events from the System and
  Application logs for the last seven days, with a filter and the top error
  sources.
- **Reliability history** (last 30 days) and **crash dumps** (minidumps and
  `MEMORY.DMP`, flagged when a blue screen happened in the last 30 days).
- **Memory diagnostic**: the last Windows Memory Diagnostic result.
- **Windows Update**: last successful install and pending-reboot flags.
- **Free space** warnings (under 10 % warning, under 5 % critical).
- **Thermal** items from the live sensors, and **security basics**
  (antivirus state, firewall profiles, BitLocker where available).
- **Deep tools** card linking to the Storage page (surface scan, `chkdsk`,
  benchmark) and the Memory page (memory test).

### Storage

Tools for every physical disk. Each device card shows model, size, media and
bus type, health badge, volumes, a *system disk* badge and a link to the
vendor's own tool (Samsung Magician, Western Digital Dashboard, SeaTools,
Crucial Storage Executive, Kingston SSD Manager, Solidigm Storage Tool, SK
hynix Drive Manager, KIOXIA SSD Utility, Corsair SSD Toolbox, ADATA SSD
ToolBox).

- **Surface scan** (read-only): *Quick* samples the first and last 2 % of the
  disk plus 200 random blocks; *Full* reads every block. Choose the block
  size, then *Start* or *Cancel*. A live block map colours each block OK,
  Slow or Bad; the page shows MB/s, bad and slow counts, elapsed time and ETA,
  and ends with a summary and what to do if bad blocks were found (back up
  now, plan a replacement). The scan never writes to the disk.
- **Benchmark**: pick a volume and a test size (256 MB, 1 GB or 4 GB). The
  app writes a temporary file with unbuffered, write-through I/O, then
  measures sequential read and write and random 4K read and write (MB/s and
  IOPS), deletes the file and compares your numbers with typical SATA SSD,
  NVMe and HDD figures. Needs free space of the test size plus 10 %.
- **Check disk**: *Scan now* runs `chkdsk X: /scan` online and streams the
  output; *Schedule full repair* runs `chkdsk /f /r` at the next restart after
  a confirmation that explains the restart.
- **Repair volume**: `Repair-Volume` in Scan, SpotFix or OfflineScanAndFix
  mode.
- **Optimize**: `Optimize-Volume` with re-trim for SSDs or defragment for
  HDDs, output streamed.
- **Disk error events**: the last 30 days of disk, NTFS, storahci, stornvme
  and volmgr events with each common event id explained.
- **Open Disk Management**.

#### Every drive, read correctly

The drive list comes from one inventory shared with the Health check and the
hardware overview. It joins MSFT_Disk, MSFT_PhysicalDisk and Win32_DiskDrive
(by disk number, serial and size) so every drive appears once: SATA hard
disks, SATA and NVMe SSDs, USB drives and USB bridges, SD cards, Storage
Spaces (and the pool member drives Windows hides, marked *Pool member*),
Intel RST / AMD RAID volumes, dynamic, offline and empty disks.

- **Facts per drive**: capacity in both units, correctly labelled
  ("4.00 TB (3.64 TiB)"), hard disk or SSD with the RPM of hard disks, the
  connection and link speed ("SATA 6 Gb/s", "PCIe 4.0 x4", with a note when
  the port runs slower than the drive can), serial (byte-swapped and
  hex-encoded serials are fixed), firmware, partition style (GPT, MBR,
  dynamic, not initialized), drive letters and partitions without a letter,
  power state and Windows' own health status.
- **Media type**: MediaType 3/4/5 (HDD/SSD/SCM); when Windows says
  *Unspecified*, the NVMe bus, the seek penalty Windows measured, the
  spindle speed (0 = SSD) and the ATA rotation rate decide.
- **SMART**: read per drive (SMART_RCV_DRIVE_DATA, ATA pass-through, SAT
  pass-through for USB enclosures, the NVMe health log). Raw values are
  decoded per maker: power-on hours from the low 32 bits (Seagate keeps
  milliseconds above), Seagate's packed command timeouts (188) and read/seek
  error rates (1, 7), temperatures from the low byte only. The table shows
  value, worst, the drive's own threshold, the decoded raw value and a
  status (OK, Watch, Below threshold, Failed).
- **Verdict**: *Good*, *Caution* or *Bad* with the reason in one sentence.
  Caution for any reallocated, pending or offline-uncorrectable sector, spin
  retries, reported uncorrectable errors, CRC errors (with "check the
  cable"), 70 % SSD wear or a warm drive; Bad when the drive predicts its own
  failure, a pre-fail attribute crosses its threshold, there are many bad
  sectors, 90 % wear, or an NVMe critical warning. When there is no reading
  the page says why: the USB enclosure blocks SMART, the RAID driver hides it
  (use Intel RST or AMD RAIDXpert), a storage space has no SMART of its own.
- **Sleeping drives stay asleep**: a hard disk that has spun down is not
  woken by a routine refresh or the Health check, and the sensor polling
  skips it until it wakes. It shows *Asleep, last reading at …* with the last reading (kept between
  runs). *Wake and read* asks first.
- Right-click a drive for *Copy details* (Ctrl+C), *Read now*, Disk
  Management and Refresh (F5).

Limitations: a full scan of a large HDD takes hours; the page says so and
suggests *Quick* first. Everything that writes (check disk, repair, optimize)
is confirmed first. SMART of drives inside a Storage Spaces pool or behind a
RAID driver cannot be read directly; some USB enclosures block SMART
entirely.

### Memory

- **Overview**: total, used and available with a gauge; commit charge and
  limit; cached and standby bytes; memory compression and SysMain state; page
  files; the module table (slot, size, rated and configured speed,
  manufacturer, part number); an **XMP/EXPO** card that says, for example,
  "Your RAM is rated for 6000 MT/s but runs at 4800. Enable XMP or EXPO in
  BIOS" with the steps; a dual-channel note; and the last Windows Memory
  Diagnostic result.
- **Warnings**: a single module, mixed sizes, XMP off, page file disabled,
  low available memory.
- **Run quick memory test**: pick what percentage of free memory to test and
  how many passes. The app fills that memory with patterns (0x55…, 0xAA…,
  walking ones, seeded random) across all cores, verifies each pass and lists
  any mismatches with advice. This is a user-mode test of memory that is
  currently free; it is not a replacement for a boot-time test.
- **Schedule Windows Memory Diagnostic**: sets `bcdedit /bootsequence
  {memdiag}` after confirmation and offers to restart. Undo hint provided.
- **Clear standby list**: purges the standby cache (useful before a benchmark
  or when a game stutters; Windows refills it over time).
- **Profiles**: *Balanced* (Windows defaults), *Gaming* (compression and
  SysMain on, fixed page file of 1.5× RAM, at least 8 GB, on the fastest SSD),
  *Low-memory PC* (2× RAM page file) and, in **Advanced**, *Workstation*
  (compression off, 1× RAM page file). Each explains why and may need a
  restart.
- **Advanced**: a manual page-file editor per drive.

### Battery

Hardware → Battery shows how worn a laptop or tablet battery is and how long a
charge lasts. It only reads; nothing on the PC changes.

- **Summary tiles**: battery health (full-charge capacity as a percentage of
  the design capacity, coloured with the same 20 % / 40 % wear limits as the
  Health page), charge cycles, full charge against design capacity in Wh, and
  how long a full charge lasts in use now and when the battery was new.
- **Capacity history**: a chart of the full-charge capacity over the life of
  the battery, one point per day or week as Windows recorded it, with the
  design capacity as a dashed line and the average loss per month.
- **Right now**: charge, whether it is charging or discharging and at how
  many watts, the time left, and the battery's name, manufacturer and
  chemistry.
- **Recent use**: the last three days from Windows' battery report, newest
  first: in use or asleep, on battery or plugged in, how long, and the charge
  before and after (the drain per hour in the tooltip).
- **Make the battery last longer** (collapsed): tips that fit the battery, such
  as a charge limit in the maker's app when the PC is mostly plugged in, battery
  saver, heat, and a replacement once it has lost 40 % or more.
- **Usage history** (collapsed): time on battery and plugged in per day or
  week, with Windows' estimate of how long a full charge lasted then.
- **Open the full report** has Windows write its detailed HTML battery report
  to `Documents\PC Checker Reports` and opens it. **Battery settings** and
  *More → Power settings* open the matching Windows Settings pages.
- **Desktops**: the page says "This PC has no battery" instead of showing
  empty charts. A UPS (battery backup) plugged in over USB is recognised and
  pointed to its maker's software.

Sources: `Win32_Battery`, `BatteryStaticData`, `BatteryFullChargedCapacity`
and `BatteryCycleCount` from WMI, the live state the Power page uses, and
`powercfg /batteryreport /xml`, whose numbers, dates and durations do not
depend on the Windows display language. A missing source becomes a warning and
the page shows what the others returned. Demo mode shows a two-year-old 57 Wh
laptop battery at 86 % health after 318 cycles.

---

## Devices

### Displays

- **Arrangement preview**: rectangles proportional to each display's
  position, with numbers and a star on the primary; **Identify** flashes a
  big number on every monitor for three seconds.
- Per display: name, connection (HDMI, DisplayPort, internal and so on),
  size, current mode, scale, HDR state with a toggle when supported,
  **orientation**, **resolution** (grouped by aspect ratio) and **refresh
  rate** (filtered by resolution), *Apply* with a 15-second *Keep these
  settings?* countdown that reverts automatically, **Set as primary**, and
  **brightness** and **contrast** sliders over DDC/CI on external monitors or
  WMI on laptop panels (hidden with a tooltip when the monitor does not
  answer).
- **Profiles**: save the current layout, apply, delete.
- Shortcuts: Windows display settings, Night light, advanced graphics
  settings, colour calibration (`dccw.exe`) and your GPU control panel
  (NVIDIA app or Control Panel, AMD Software, Intel Graphics Software).

Limitations: variable refresh rate is controlled in Windows Graphics settings;
the page links there. Many monitors ignore or answer DDC/CI slowly; the page
says when it gave up.

### Audio

- **Output devices** and **Input devices** as cards: name, interface, default
  and communications badges, state, format, a volume slider with mute, a live
  level meter, *Set as default*, *Set as default for calls*, and *Properties*
  (opens the Sound control panel).
- **Per-app volume mixer**: every audio session with its own slider and mute.
- **Quick tools**: *Restart audio service* (confirmed), *Run troubleshooter*,
  *Open sound settings*, *Open volume mixer*, *Open control panel*.

Limitations: exclusive mode, enhancements and spatial sound are per-device
properties in Windows; the page links to them rather than duplicating them.

### Keyboard & Mouse

- **Mouse**: pointer speed (1–20, shown as the familiar "6/11"), *Enhance
  pointer precision* with an explanation of acceleration and why gamers turn
  it off, double-click speed with a test target, swap buttons, scroll lines or
  one screen, pointer trails, hide pointer while typing, snap to default
  button. *Apply* and *Revert*.
- **Keyboard**: repeat delay and rate with a test box, cursor blink rate,
  Sticky Keys, Filter Keys, Toggle Keys.
- **Polling rate**: *Measure* for five seconds using raw input and shows the
  live and final Hz with an interpretation (125, 250, 500, 1000, 4000, 8000 Hz
  and what each is good for).
- **Profiles**: *Windows default*, *Gaming*, *Precision work*,
  *Accessibility*, each applied with one click after confirmation.
- **Detected devices** with links to the vendor's software (Logitech G HUB,
  Razer Synapse, Corsair iCUE, SteelSeries GG, Glorious Core, ASUS Armoury
  Crate, HyperX NGENUITY, Microsoft Mouse and Keyboard Center, Wootility,
  Keychron Launcher) and shortcuts to the Windows mouse, typing and
  control-panel pages.
- RGB for keyboards and mice lives on the **Lighting** page; this page links
  to it.

Limitations: DPI, button mapping and onboard profiles live in the vendor's
software; the app cannot change them.

---

## Cooling & RGB

### Fans

Finds every fan that LibreHardwareMonitor exposes a control for: motherboard
headers on supported Super I/O chips, NVIDIA and AMD GPU fans, and some
coolers and controllers.

- An info bar lists what can be controlled on this PC and what cannot (for
  example "GPU fans: via NVIDIA driver; case fans: Nuvoton NCT6798D on
  motherboard"), or says plainly that no controllable fans were found.
- **Fan cards**: name, RPM, percent, mode badge and a mode selector: *Auto*,
  *Manual* (slider, clamped to the minimum in Settings unless you explicitly
  allow lower), or *Curve*.
- **Curve editor**: a 0–100 °C by 0–100 % chart with draggable points,
  double-click to add or remove, a temperature source picker, hysteresis and
  ramp-step boxes, and a live marker showing the current temperature and the
  speed the curve produces.
- **Profiles**: *Silent*, *Balanced* and *Performance* built in, plus your own
  saved to Settings. *Apply*, *Save as…*, *Revert all to auto*, and the
  *Revert to automatic on exit* toggle.
- **Failsafe**: if any temperature crosses its critical threshold while a
  manual or curve profile is active and the fan is below 100 %, the app
  forces 100 % and raises an alert.
- **Watchdog**: while software control is active the app keeps a session
  file; if it finds one on the next start (a crash), it reverts graphics card
  fans to automatic and tells you on the Dashboard. Motherboard fans keep the
  speed they were left at until the PC restarts, so the app holds them on the
  Balanced curve with its overheating protection on until you restart.
- **Advanced**: the raw control list with identifiers, ranges and current
  software values.

Limitations: laptops and most OEM desktops do not expose fan controls to
software; AIO pumps and RGB controllers that only speak a vendor protocol are
not controllable. See [SUPPORTED-HARDWARE.md](SUPPORTED-HARDWARE.md).

### Lighting

RGB control through [OpenRGB](https://openrgb.org), one open-source service
that speaks to hundreds of motherboards, RAM kits, GPUs, keyboards, mice,
coolers and strips.

- **Status card**: connection state, server address and OpenRGB version or
  what is wrong in plain words (not installed, installed but not running,
  open with its SDK server off, not answering at the address), *Connect* /
  *Reconnect*, *Start OpenRGB* (runs it minimized with `--server`, only from
  Program Files), an optional *Install OpenRGB* (winget package
  `CalcProgrammer1.OpenRGB`, for all users, after a confirmation, journaled),
  *Download OpenRGB*, a *Why OpenRGB?* note and the vendor suites as
  alternatives (ASUS Armoury Crate, MSI Center, Gigabyte Control Center,
  ASRock Polychrome, Corsair iCUE, Razer Synapse, Logitech G HUB, SteelSeries
  GG, NZXT CAM). While OpenRGB is away, PC Checker retries in the background
  with growing pauses, so starting OpenRGB by hand connects the page too.
- **All RGB devices**: one list of every device, controllable or only found,
  with an icon, brand, type, where it was found, a coloured status (*Ready*,
  *Ready once a zone size is set*, *Found, control needs OpenRGB*, *Found, but
  iCUE is using it*, *May have lighting*) and one line saying why and what to
  do next. *Rescan* (F5) runs the detection again; it changes nothing.
- **Detection without OpenRGB**: every present USB, HID and PCI device is
  matched against the IDs of the more than 1,100 USB lighting controllers and nearly 900
  graphics cards OpenRGB supports (ASUS Aura, MSI Mystic Light, Gigabyte RGB
  Fusion, Corsair, NZXT, Razer, Logitech, SteelSeries, HyperX, Cooler Master,
  Lian Li, Thermaltake, ASRock and many more). HID interfaces without IDs in
  their path are asked with `HidD_GetAttributes`, and Windows Dynamic
  Lighting (LampArray) devices are recognised by their HID usage. Memory kits
  are recognised by part number (G.Skill Trident Z RGB/Neo/Z5 RGB, Corsair
  Vengeance RGB and Dominator Platinum RGB, Kingston FURY RGB, HyperX RGB);
  ASUS, ASRock, Gigabyte and MSI boards without a USB lighting controller get
  a "may have lighting" hint, because their lighting sits on the SMBus or a
  Super I/O chip that only OpenRGB reaches. IDs from chip makers that other
  products share are marked *likely*, not certain.
- **Apps that can block lighting control**: running iCUE, Armoury Crate (its
  LightingService), MSI Center / Mystic Light, Gigabyte Control Center / RGB
  Fusion, SignalRGB, NZXT CAM, Razer Synapse, Logitech G HUB, SteelSeries GG,
  Lian Li L-Connect and TT RGB Plus are found by process and service name,
  with what they hold. *Close for this session* asks first (and says when
  the app also drives fans or a pump), stops its services and ends its
  processes until the next restart; nothing is uninstalled and start-up
  settings are not changed.
- **Top controls**: *Sync all* colour picker, *Apply to all*, brightness for
  all, *Turn off all*, profile picker with *Apply*, *Save* and *Delete*.
- **Device cards**: type icon, name, vendor, LED count and zones; a mode
  picker; a colour swatch and picker; speed and brightness sliders when the
  mode supports them; a per-zone expander with zone colours. Addressable
  headers and strips (resizable zones, which start at 0 LEDs) get an LED
  count box and *Set size*. Changes apply immediately with a short debounce.
- **Reading OpenRGB defensively**: zones keep their minimum and maximum LED
  counts and their position, the LED count falls back to the zones when the
  LED list is empty, and when the SDK client cannot read a device the others
  are read one by one and the odd one is read by PC Checker's own reader for
  protocol versions 0 to 5 (length-checked, newer fields ignored) and listed
  as "details unreadable" instead of hiding the whole list.
- **If a device is missing**: a collapsed section with the usual fixes:
  start the SDK server, close the maker's app, Armoury Crate's
  LightingService, PawnIO and administrator rights for RAM and motherboard
  lighting on the SMBus, sizing addressable headers, graphics cards, and
  unsupported models.
- **Peripherals** section groups keyboards, mice and headsets.
- **Profiles**: *Gaming (blue/cyan)*, *Work (warm white, 40 %)*, *Night
  (off)*, *Rainbow* built in, plus your own.

Limitations: colours and effects need OpenRGB running with its SDK server
enabled; without it the page lists and explains devices but cannot change
them. Windows does not expose the devices on the SMBus (RAM and many
motherboard and graphics card lighting chips), so those are recognised by part
number, brand or card ID, and only OpenRGB can confirm them. Devices OpenRGB
does not support cannot be controlled; use the vendor's suite. Lighting
changes are cosmetic, so they are journaled but do not create restore points.

---

## Apps & Drivers

### Software

- **Scan**: programs from the 64-bit, 32-bit and per-user registry plus Store
  apps, with version, publisher, install date, size, location and source
  badge. *Calculate sizes* measures folders that did not report a size.
  *Export CSV*.
- **Installed alongside**: programs installed within minutes of another with
  a different publisher, or installed inside another program's folder, or on
  the known-bundler list, are grouped with the reason. Runtime dependencies
  (Visual C++, .NET) are grouped separately so they are not mistaken for
  bloat. *Uninstall extras* removes a bundle's children.
- **Uninstall** (confirmed): quiet with the installer's silent switch (MSI,
  Inno Setup, NSIS, InstallShield, Squirrel, Store) or normal with the
  vendor's wizard. Exit codes are translated (reboot required, cancelled, not
  installed).
- **Protected** entries (Edge, WebView2, Defender, .NET and Visual C++
  runtimes, driver packages) show a lock with the reason. **Advanced** lets
  you override for non-critical ones with a warning.
- **Find leftovers**: after a program is uninstalled the page offers to look
  for what it left behind; the Leftovers tab opens and scans with that
  program's traces marked *Just uninstalled* and ticked.

### Leftovers

- **Scan** (read only, cancellable) compares what is on disk with every
  installed program and Store app (names, makers, package names and install
  folders, with fuzzy matching that ignores versions, "x64", brackets and
  "Inc."/"GmbH"). It reports:
  - folders in Program Files, Program Files (x86), ProgramData and
    AppData\Local, Roaming and LocalLow that nothing installed uses, one level
    deeper inside a maker's folder that still has other programs, and empty
    folders uninstallers left;
  - registry keys under `HKLM\SOFTWARE`, `HKLM\SOFTWARE\WOW6432Node` and
    `HKCU\Software` of makers or products that are no longer installed;
  - services whose program file is gone, scheduled tasks that start a missing
    program, and Start menu shortcuts to missing targets.
- **Each finding** says what it is, why it looks orphaned, its size and a
  confidence: **High** (empty folders, things whose whole program folder is
  gone, traces of the program just uninstalled), **Medium** (probably left
  behind) or **Low** (recently changed, or inside a maker's folder that still
  has programs). Only High is ticked; *Select recommended* ticks it again.
- **Never reported**: Windows and Microsoft folders and keys, shared folders
  (Common Files, Package Cache, InstallShield data, Store packages), NVIDIA,
  Intel, AMD, Realtek and other driver parts, game libraries (Steam, Epic,
  GOG, Ubisoft, Xbox), developer caches, GUID-named installer folders,
  hidden dot-folders, links (junctions) and anything outside the listed
  folders. Services and shortcuts that point into the Windows folder are left
  to Windows.
- **Remove selected** (confirmed) creates a restore point first and checks
  every item again right before touching it: folders and shortcuts go to the
  Recycle Bin; registry keys are exported to one sealed `.reg` backup (the
  Registry page's format, so it also appears under Registry → Backups);
  scheduled tasks are exported to XML and deleted; services are deleted with
  `sc.exe` only while their program is still missing and they are stopped.
  Folders are only removed one or two levels inside the listed roots, never
  through or inside a link. Every item is written to the Action Journal.
- **Restore list** puts removed registry keys and scheduled tasks back. Task
  backups are sealed in `HKLM` and only imported unchanged, through a folder
  only administrators can write to, and never over an existing task.
- **Limitations**: folders of portable programs (which have no uninstall
  entry) can show up; they are scored Low or Medium when they hold programs
  or changed recently, so check before ticking them. A folder Windows will
  not let PC Checker read completely is not reported. Per-user folders and
  keys are those of the account PC Checker runs as.

### Drivers

- **Scan drivers**: every installed driver with device, class, provider,
  version, date, age, signature, status and problem code. Filters for
  third-party only, problems only and old only; a search box; a row expander
  with hardware ids, `.inf` name and location.
- **Check for updates** searches several sources side by side and shows one
  list grouped by device. Each device shows its installed version and every
  source's newest version, release date, size and source; the newest is
  marked *Newest*, and a radio button picks the source per device (the newest
  is chosen by default; on a tie Windows Update wins). Versions are compared
  in each maker's own numbering: NVIDIA's 560.94 is the last five digits of
  the Windows version 32.0.15.6094; AMD's Adrenalin version (24.12.1) cannot
  be derived from the Windows version (32.0.21013.1000), so the app reads the
  `RadeonSoftwareVersion` the AMD driver records and otherwise says it cannot
  compare.
  - **Windows Update**: the driver catalog through the Windows Update Agent.
    *Download only* and *Install selected* work on the ticked devices whose
    chosen source is Windows Update (confirmed; restore point; reboot bar
    afterwards with *Restart now* or *Later*).
  - **NVIDIA**: NVIDIA's driver service (the one behind the NVIDIA app and
    nvidia.com's driver search) is asked by the card's PCI device id, so
    GeForce, RTX/Quadro and notebook GPUs all get their own package. *Game
    Ready* or *Studio* is chosen under *Update sources*.
  - **AMD**: the current AMD Software: Adrenalin Edition release from AMD's
    official driver page; the package is AMD's auto-detect installer, which
    picks the driver for the Radeon GPU or Ryzen APU while it runs.
  - **Intel**: Intel publishes no public version feed, so Intel graphics (Arc
    and Iris Xe, or the 7th–10th gen package), Wi-Fi and Bluetooth link to
    their exact Intel download page with your installed version alongside.
    Intel's Driver & Support Assistant is offered too (open it, or install it
    with winget).
  - **PC maker tools**: Dell Command | Update and SupportAssist, HP Support
    Assistant and HP Image Assistant, Lenovo Vantage and System Update,
    MyASUS and Armoury Crate, MSI Center and GIGABYTE Control Center are
    found by their uninstall entries, program folders and Store packages.
    Installed ones get *Open*; missing ones get *Install with winget* where
    the tool is on winget (the exact command is shown, and it only runs when
    winget's own program is found in its protected folder), and every maker
    gets its support page for your exact model (from the PC's maker and model,
    or the motherboard on self-built PCs).
- **Download and install** (NVIDIA and AMD packages): asks every time and
  shows the file, its size and the server. The file is only fetched over
  HTTPS from the maker's own download servers (every redirect is checked
  against the same list), into a fresh folder only administrators can change.
  Before it runs, Windows checks its Authenticode signature with the full
  certificate chain and revocation, and the signer must be NVIDIA Corporation,
  Advanced Micro Devices, Inc. or Intel Corporation; anything else is deleted
  without running. A restore point is created, then the maker's installer
  runs: NVIDIA's can run silently or as a silent clean install with NVIDIA's
  documented switches (`-s`, `-clean`, `-noreboot`), AMD's always opens.
  Afterwards the download folder is removed and the driver list refreshed.
- **Update sources** (collapsed at the end of the Updates tab): turn each
  source on or off (all on by default), pick the NVIDIA branch and installer
  mode, and choose whether vendor downloads ask each time (default) or never
  happen (the vendor's page opens instead).
- When a source cannot be reached, its page changed or it does not know a
  device, the other sources still show, a note says what happened, and the
  device gets *Open the vendor page*.
- **Back up all drivers** to a folder you pick (`pnputil /export-driver`).
- **Old drivers** (its own tab): superseded driver-store packages (same
  original INF, provider and class with a newer date or version), packages no
  connected device uses, and drivers of disconnected devices. Grouped as Safe
  to remove, Probably safe and Keep, with sizes from the driver store and the
  total space you can free. Removal exports each package to
  `%LOCALAPPDATA%\PcChecker\driver-backups\<time>` first, forgets
  disconnected devices with `pnputil /remove-device`, and deletes packages
  with `pnputil /delete-driver` (never forced).
- **Vendor links**: NVIDIA, AMD and Intel driver pages for your GPU and
  chipset, your motherboard vendor's support page, and *Windows Update
  optional updates*.
- **Advanced**: *Install .inf*, *Roll back* (where the app can map the device
  to its driver package; otherwise it opens Device Manager and explains) and
  the driver store list.
- Summary tiles: total, third-party, with problems, older than three years,
  unsigned, updates available.

Limitations: Intel versions are not compared automatically (no public feed);
AMD versions are only compared when the AMD driver records its Adrenalin
version; vendor packages are never run unless signed by the maker. The app
never downloads from third-party driver sites.

### Drivers: your PC, chipset and BIOS from the maker

The Updates tab starts with a **Your PC** card, and three more sources join
the driver search so the makers of the board, chipset and network, audio and
Bluetooth chips are asked too.

- **Your PC**: the board or PC model read from SMBIOS (Win32_ComputerSystem,
  Win32_ComputerSystemProduct, Win32_BaseBoard, Win32_BIOS,
  Win32_SystemEnclosure), with maker names cleaned up ("ASUSTeK COMPUTER
  INC." → ASUS, "Micro-Star International Co., Ltd." → MSI, "Gigabyte
  Technology Co., Ltd." → GIGABYTE) and firmware placeholders ignored. A
  self-built PC is shown by its motherboard; a brand PC by its model.
- **BIOS**: version and release date, with *This BIOS is N years old* when it
  is more than two years old. For Lenovo, the newest BIOS for the model is
  read from Lenovo's public catalog and compared. PC Checker never updates a
  BIOS: it shows the maker's page and a plain note on flashing safely.
- **Support page with the exact model filled in**: ASUS, MSI, GIGABYTE/AORUS
  (by board revision) and ASRock boards; Dell by service tag; Lenovo by
  machine type; HP by model; Acer, Surface and Framework driver pages. BIOSTAR,
  NZXT and a GIGABYTE board without a revision get the maker's download centre
  with a note on what to search for.
- **Chipset driver**: AMD Chipset Software (the installed version from Apps)
  compared with AMD's chipset page for the socket (AM5, AM4, sTRX4, sTR5). When
  AMD has a newer one, *Get AMD's driver* downloads it from AMD's server and
  runs it through the same signed-package flow as graphics drivers (signature
  must be AMD's, restore point first, you confirm). Intel chipsets link to
  Intel's Chipset INF Utility page and to the PC or board maker for the
  Management Engine driver, with Intel's assistant as a second option.
- **Network, audio and Bluetooth**: Intel Ethernet and Killer (Intel) link to
  Intel's pages, Realtek Ethernet to Realtek's download page; MediaTek,
  Qualcomm and Realtek Wi-Fi and Bluetooth, and onboard Realtek audio, link to
  the board or PC maker's page. Audio is never pointed at a generic Realtek
  driver, because the maker's driver carries the jack, microphone and effects
  settings for the model.
- **Lenovo catalog**: for Lenovo PCs, drivers from the model's catalog
  (`download.lenovo.com/catalog/<machine type>_Win11.xml`) are matched to
  the installed devices by hardware id and compared with the installed
  version; install them with Lenovo's tool or from the support page.
- Right-click the card to copy the details (no serial number), open the
  support page or check again. Each source has its own switch under **Update
  sources** (*Chipset (AMD, Intel)*, *Network, audio and Bluetooth*, *BIOS and
  PC maker catalogs*).
- All lookups use HTTPS to allow-listed hosts only (www.amd.com,
  download.lenovo.com) with a 30-second timeout. Answers are kept for 12 hours
  in `%LOCALAPPDATA%\PcChecker\cache\driver-lookups`, so a second search does
  not ask again and an offline PC still shows the last answer, marked as saved.

Limitations: Intel, Realtek, MediaTek and Qualcomm publish no version list PC
Checker can read, so those devices get a link and the version is compared on
the maker's site. Dell's catalog is too large to download for one check and
HP's needs CAB extraction, so Dell and HP get the exact support page instead.
MSI, ASUS, GIGABYTE, ASRock and other board makers publish no BIOS list
reachable without an account, so their BIOS version is compared on their page.

### Windows Update

The Windows Update tab (Apps & Drivers, after Old drivers) covers software
updates; driver updates stay on the Drivers page.

- **Status tiles**: updates waiting (and how much to download), last check,
  last successful install (from the Windows Update Agent, or the history when
  Windows did not record it) and whether updates are paused. A restart bar
  appears while Windows waits for a restart to finish an update.
- **Set by policy**: when Group Policy or device management
  (MDM) sets Windows Update policies, an info bar lists them in plain English
  (pause disabled, active hours set, automatic updates off, an update server,
  deferrals, a pinned Windows version). Controls that a policy owns are shown
  read-only. With an organisation's update server (WSUS) configured, *Check
  for updates* asks that server instead of going around it.
- **Updates** tab: *Check for updates* asks Windows Update (the same service
  id the Drivers page uses) for software that is not installed yet. Each
  update shows its KB article, size, type (cumulative, security, feature
  update, definitions or other, decided from Windows Update's
  language-independent category ids), Microsoft's severity rating and
  whether it needs a restart. Optional updates (previews, new Windows
  versions) are never ticked for you, and updates that ask questions while
  they install are left to Settings. *Install selected* and *Install all*
  confirm first, create a restore point (per Settings; the install does not
  start if it fails), then download and install one update at a time with
  progress. Cancel stops before the next update. Every update gets its own
  result with the error explained in plain English (for example 0x800F0922,
  0x80070070, 0xC1900101), and installed ones leave the list.
- **History** tab: the last 100 installs, newest first, with type, result and
  date. Failed and cancelled installs are marked red and their row explains
  what went wrong; *Failed only* filters the list. *Update history in
  Settings* opens the Settings page where updates can be uninstalled.
- **Pause & active hours** tab:
  - *Pause updates* for 1 to 5 weeks, or *Extend pause* while paused. PC
    Checker writes the same values the Settings app writes
    (`PauseUpdatesStartTime`, `PauseUpdatesExpiryTime` and the feature and
    quality pairs under
    `HKLM\SOFTWARE\Microsoft\WindowsUpdate\UX\Settings`, as UTC times like
    `2026-11-03T09:30:15Z`), keeps the original start when extending and
    never goes past the limit Windows allows (35 days from the start, or
    `FlightSettingsMaxPauseDays`). The values are read back after writing;
    if Windows did not keep them, on a build older than Windows 10 version
    2004, or when a policy owns pausing, the page offers to open Settings
    instead. *Resume* removes the values, as Settings does.
  - *Active hours*: start and end hour, at most 18 hours apart (fewer when
    the ActiveHoursMaxRange policy says so) and allowed to run past
    midnight. Saving writes `ActiveHoursStart`/`ActiveHoursEnd` and the
    hand-picked pair `UserChoiceActiveHoursStart`/`UserChoiceActiveHoursEnd`
    that Settings restores them from. While Windows adjusts active hours
    automatically, or when saving fails, the page sends you to Settings
    instead. The read-back only confirms the values were stored, so the
    result also says to check Settings.
  - Every change is confirmed and recorded in the Action Journal with the
    previous value; pause and active hours do not create restore points.
- **Roll back** tab (going back after an update). The lists are read the
  first time the tab opens (DISM takes a moment); *Refresh* reads them again.
  - *Installed updates*: every update `Get-HotFix` (`Win32_QuickFixEngineering`)
    lists, with its KB, title and type from the Windows Update history and
    the install date, plus the cumulative update's package from
    `DISM /Online /Get-Packages`. Each row says how it would be removed:
    - standalone updates (.NET, ordinary and security updates) with
      `wusa.exe /uninstall /kb:<n> /quiet /norestart`;
    - the monthly Windows cumulative update with
      `DISM /Online /Remove-Package /PackageName:<Package_for_RollupFix…> /Quiet /NoRestart`,
      because wusa cannot remove the combined servicing stack and cumulative
      updates Windows 10 and 11 ship. The package is the installed RollupFix
      whose version matches the build in the update's title (for example
      `(26100.4061)`), or the newest installed one when the title has no
      build. Releases that ship as an enablement package are matched on the
      build they are serviced from (25H2 `26200` on `26100`, 23H2 `22631` on
      `22621`, Windows 10 22H2 `19045` on `19041`) with the same revision.
      Cumulative updates that a newer one replaced cannot be removed;
    - servicing stack updates are refused with the reason: Windows marks them
      permanent and needs them for every later update. They are recognised by
      their title, and, in any display language or without an update history,
      by the DISM package list: an update with no package of its own while a
      `Package_for_ServicingStack` package is installed is refused too.
    *Uninstall* explains what is lost, creates a restore point (the removal
    does not start if it fails), runs the tool from the system folder and
    reports the result. It never restarts; a restart bar offers *Restart now*
    when Windows needs one to finish. Afterwards it offers to hide the update
    so Windows Update does not reinstall it.
  - *Hide this update* (on a pending update's row in the Updates tab, or on an
    installed one) sets `IUpdate.IsHidden`, the flag Microsoft's "Show or
    hide updates" troubleshooter uses: Windows Update no longer offers or
    installs it. *Hidden updates* lists them with *Show again*. An update is
    found by its Windows Update id (from the check or the install history),
    or by KB among the updates that are not installed; required updates
    cannot be hidden.
  - *Go back to the previous version of Windows*: after a feature update,
    `DISM /Online /Get-OSUninstallWindow` gives the window (10 days by
    default), counted from the upgrade date (`InstallDate`, or when
    `Windows.old` was created) to show how many days are left. *Go back…*
    asks twice (back up your files; apps installed since may need
    reinstalling; you need the old password; it cannot be undone here), then
    runs `DISM /Online /Initiate-OSUninstall /Quiet /NoRestart` and offers
    the restart that carries it out. *Keep the option for* sets
    `DISM /Online /Set-OSUninstallWindow /Value:<14–60>`. Without
    `C:\Windows.old`, or when DISM says the option has gone, the card says
    there is nothing to go back to. Storage Sense, Disk Cleanup or deleting
    Windows.old ends the option; the Cleanup page's Windows.old category says
    the same.
  - *Open Drivers*: driver updates are rolled back on the Drivers page.
  - wusa and DISM print translated text, so results are decided from their
    exit codes only (0, 3010 and 1641 restart needed, 2359303 and 0x80070490
    not installed, 0x80240017 not applicable, 0x800F0825 permanent, 0x800F0806
    restart first, 5 and 740 access denied, 1618 another install running, and
    so on), each with a plain-English message. DISM runs with `/English` and
    its package list is parsed by the package identities, which are never
    translated.
  - Uninstall, hide, show again, go back and the window change are recorded
    in the Action Journal; only *Uninstall* creates a restore point (going
    back replaces Windows together with its restore points).
- **Links**: Windows Update, Update history, Advanced options, Active hours
  and Recovery in the Settings app.

Limitations: the history is what the Windows Update Agent recorded (updates
installed by other tools such as DISM or a standalone installer are not in
it); restarts are left to you or to Windows outside active hours. Going back
is only possible within Windows' own window and while Windows.old exists; an
update Windows Update no longer lists cannot be hidden until it is offered
again.

### Startup

- **Startup apps**: registry Run keys, startup folders and logon tasks with an
  enabled toggle, name, publisher, command, kind and location; search;
  *Add…*, *Remove* (confirmed; shortcuts are moved to a backup folder, not
  deleted) and *Open file location*.
- **Scheduled logon tasks** in their own grid.
- **Advanced**: *Services at startup* with a start-mode picker; critical
  Microsoft services are protected and cannot be disabled.
- **Boot options**: last boot time and duration, *Fast startup* toggle with
  the pros and cons, *Boot menu timeout*, *Safe mode next boot* (danger
  styling, confirmed, with the way back explained), a link to schedule the
  memory diagnostic, *Restart to UEFI/BIOS* and *Advanced startup* (both with
  a countdown and a cancel).
- Shortcuts: Windows Startup Apps settings, Task Scheduler, System
  Configuration.

### Scheduled tasks

Every Windows Task Scheduler task, in the *Apps & Drivers* section next to
Startup.

- **List**: name and folder, status (Ready, Running, Off), when it runs in
  plain words ("At log on", "Daily at 03:00, then every hour for 1 day",
  "When idle", "Weekly on Mon, Wed at 09:00"), last run with its result in
  plain words (OK, "Running now", "Not started: it was already running",
  "Failed: the program it runs was not found", other codes as hex with
  Windows' own message), next run and who it is from (the program's signer,
  else the task's author).
- **Filters**: *Not from Microsoft* (the default: updaters, PC-maker tools,
  adware, plus any task with a warning), *All tasks*, *Microsoft*, *With
  warnings* and *Turned off*, and a search over name, program and company.
- **Warnings** with a plain reason: the program runs from AppData, Temp,
  the Public folder, Downloads or ProgramData and is not validly signed; a script host
  (PowerShell, wscript, mshta…) runs an encoded command or downloads code;
  the task is hidden from Task Scheduler's list (outside Microsoft's
  folder); the program has no valid signature. A task that only claims to be
  from Microsoft (its program is unsigned or signed by someone else) is not
  treated as Microsoft's; this is checked for Windows' own folder too
  whenever the program lies outside the Windows folder. A note explains tasks whose program is gone.
- **Details card**: what it runs, as which account (SYSTEM, a user, highest
  privileges), every trigger, the last result and next run.
- **Actions** (right-click, *More*, buttons and shortcuts): *Run now*
  (Ctrl+R), *End* (Ctrl+E), *Turn off* / *Turn on* (Ctrl+D), *Delete*
  (Del), *Open file location* (Ctrl+O), *Search online*, *Copy details*
  (Ctrl+C) and *Open Task Scheduler*. Every action asks first and is
  recorded in the Action Journal.
- **Deleted tasks**: only other companies' tasks can be deleted, and each
  one's definition is saved (sealed where only administrators can write)
  before it goes; *Restore* registers it again exactly as it was.
- Windows' own tasks (`\Microsoft\Windows\`) are read-only; Advanced mode
  allows turning them off or on, with a warning and a restore point first.
  PC Checker's own schedules stay in Settings → Schedules.

Read through the Task Scheduler COM API (numbers, dates and each task's XML
definition), so it works the same on every Windows language; when that is
not available, `schtasks /Query /XML ONE` gives the definitions without the
live state.

### Windows Features

Three sections with a plain-English note on what each is:

- **Optional features** (`.NET Framework 3.5`, Hyper-V, Virtual Machine
  Platform, Windows Subsystem for Linux, Sandbox, SMB 1.0 with its risk note,
  Telnet client, legacy components, Print to PDF and so on) with state and
  risk badges and *Enable* / *Disable*.
- **Capabilities** (OpenSSH client and server, Notepad, Paint, WordPad, Steps
  Recorder, XPS Viewer, language packs, RSAT tools and so on).
- **Built-in apps**: friendly name, package name, description, a risk label
  (*Safe to remove*, *Think twice*, *Not recommended*) and *Remove*
  (confirmed; **Advanced** adds *also remove for new users*). Removing the
  Xbox app breaks Game Bar; the Store is marked *do not remove*.

Every change is confirmed, creates a restore point and shows a reboot bar
when needed.

---

## Clean up

### Cleanup

- **Scan** by category, in seven groups you can filter by:
  - **Windows**: your temp files; Windows temp (including the Windows 11
    24H2 `SystemTemp` folder and the service profiles' Temp folders when
    elevated); Windows Update downloads; Delivery Optimization; old prefetch
    data (entries unused for 30 days only); thumbnails; icon cache (including
    the legacy `IconCache.db`); Recycle Bin; crash dumps (including
    `LiveKernelReports` GPU timeout dumps and wherever Startup and Recovery
    writes dumps); error reports; Windows logs (also Windows Update, USO,
    Delivery Optimization, `setupapi` and debug logs); font cache (the Font
    Cache service is paused); Internet temporary files; Store app temp
    files; Windows Installer leftovers (`Config.Msi`, `Installer\*.tmp`) and
    (**Advanced**) the `$PatchCache$`; the Remote Desktop bitmap cache; and
    old CHKDSK fragments on every fixed drive.
  - **Windows tools** (run through Windows' own tools, never file by file):
    Windows Update cleanup with **DISM** `/StartComponentCleanup` (size from
    DISM's own analysis) and (**Advanced**) `/ResetBase`; selected **Disk
    Cleanup** handlers (upgrade logs, downloaded program files, offline
    pages, retail demo content, Diagnostic Data Viewer files, Feedback Hub
    archives, Microsoft Defender temporary files, BranchCache), run once with
    `cleanmgr /sagerun` and unflagged afterwards; (**Advanced**) the previous
    Windows installation, Windows upgrade leftovers (`$WINDOWS.~BT`,
    `$WINDOWS.~WS`, ESD, `$GetCurrent`) and every System Restore point
    except the newest (other shadow copies are never touched). What these
    tools freed is read from the drive's free space afterwards.
  - **Graphics drivers**: DirectX shader cache; **NVIDIA** shader caches
    (`PerDriverVersion\DXCache`, `ComputeCache`, `NV_Cache`, Store and Game
    Pass caches); **AMD** shader caches (every `AMD\*Cache` folder such as
    `DxCache`, `Dx9Cache`, `DxcCache`, `GLCache`, `VkCache`, plus
    `RadeonSoftware\vkcache` and Store and Game Pass caches); **Intel** Arc
    and Iris shader caches; AMD Software, NVIDIA app and Intel Graphics
    Command Center logs and web caches; and installer leftovers in
    `C:\AMD` (folders an installed program's uninstaller needs are always
    kept), `C:\NVIDIA`, NVIDIA driver downloads and Intel's graphics
    installer files.
  - **Browsers**: Chrome, Edge and Brave (every channel: Beta, Dev, Canary),
    Firefox, Opera and Opera GX (separate cards), Vivaldi, Chromium,
    Waterfox, LibreWolf, Floorp, Zen, Arc and Yandex. Chromium caches include
    Service Worker CacheStorage, Dawn and the shared shader caches; Firefox
    caches include `startupCache`, thumbnails, `shader-cache` and crash
    reports.
  - **Apps**: Discord (stable, PTB, Canary), Microsoft Teams (new and
    classic), Spotify (never downloaded songs), Office caches and the
    (**Advanced**, your account only) upload and add-in cache, VS Code, Slack, Zoom, OBS Studio,
    Thunderbird and the (**Moderate**) Adobe media cache.
  - **Game launchers**: Steam (web cache, logs, dumps, client packages;
    the depot cache, per-game shader pre-caches in every library and
    abandoned downloads as separate cards), Epic Games Launcher, Battle.net,
    EA app, Ubisoft Connect (and **Advanced** installers) and GOG Galaxy.
  - **Developer tools** (**Advanced**): npm and Yarn, pip, NuGet, Java,
    .NET temporary files, Gradle, Maven, Go and Cargo caches.
  - **Privacy** (opt-in, never scheduled, your account only): recent items, jump lists, Office
    recent documents, the clipboard and its history, and browser cookies
    (**Advanced**), history and saved sessions.
- Each category is a card with a checkbox, a risk badge, its group, size and
  file count after the scan, a tip where there is a side effect, and an
  expander with the largest files and *Open folder*.
- A category whose app is running (matched by process name and install
  folder, so Chrome and Chromium or Opera and Opera GX are told apart), or
  whose precondition is not met (administrator rights, a pending restart, an
  install in progress), is skipped with the reason instead of failing.
- **Dry run** shows exactly what would be removed. **Clean selected** then
  deletes file by file and reports what was freed, skipped (locked or in use)
  and failed.
- Shortcuts: *Open Storage settings* and *Run Windows Disk Cleanup* for
  Windows.old. Its card warns that removing it, including through Storage
  Sense or Disk Cleanup, ends the option to go back to the previous version of
  Windows (Windows Update → Roll back shows the days left).
  and failed. When File Explorer kept icon or thumbnail caches open,
  *Restart Explorer* is offered.
- Shortcuts in *More*: Storage settings, Storage Sense, Windows Disk Cleanup,
  cleanup recommendations, old driver packages (Drivers page), the
  hibernation file (Optimize page), System Protection settings and Windows'
  *Delete diagnostic data*.

Safety rules: never outside the category folders and their file patterns,
never inside a category's excluded folders (for example Spotify downloads,
Ubisoft ownership data or the `C:\AMD` chipset uninstaller), never through
junctions or symbolic links, never files modified in the last 60 minutes
(configurable), never `desktop.ini`, `thumbs.db`, `ntuser.dat`, the page,
swap or hibernation file, never saved passwords, cards or bookmarks, and app
caches are skipped while the app runs (the AMD and NVIDIA installer folders
while an installer runs, AMD Software logs while AMD Software is open). What
is deliberately not offered, and why, is listed in
[SAFETY.md](SAFETY.md#cleanup-deliberately-not-offered).

### Disk space

- **Drives**: every fixed drive as a card with a used/free bar. Click one to
  measure it, or *Choose folder…* to measure a single folder. The scan reads
  with several threads at once and can be cancelled; it never changes
  anything.
- **Summary**: what was scanned, the space used, the largest folder and the
  number of files and folders. A note explains gaps: folders Windows keeps
  private, space Windows reports that no file accounts for, links that were
  not followed and OneDrive online-only files (counted, but they take no
  space).
- **Folders**: the sub-folders of the current folder, largest first, with a
  bar for their share. Click a name to go deeper; the breadcrumb and the up
  button go back. The files directly in a folder show as one line.
- **Largest files**, **File types** (video, images, games, archives and disk
  images, installers, Windows and programs, other) and **Old files** (big
  files nobody opened or changed for a year; size and age are options).
- **Duplicates**: *Find duplicates* compares the files from the last scan by
  size, then a quick SHA-256 of the first and last 64 KB, then a full SHA-256.
  Groups are listed by wasted space; *Keep newest* and *Keep oldest* tick
  every other copy. Hard links to the same data count once.
- **Actions** on every row: *Show in File Explorer* and *Copy path*. Tick
  files or folders in any list and *Move to Recycle Bin*; the confirmation
  lists the items and their total size, and warns about game folders and
  OneDrive items.

Safety rules: items only ever go to the Recycle Bin, never straight to
deletion, and only on drives inside the PC (removable and network drives have
no Recycle Bin). If Windows cannot recycle an item it asks before deleting it.
The Windows folder, Program Files, ProgramData, the profile folders
themselves (and their AppData folders), Windows.old, restore points, the
Recycle Bin, the page and hibernation files, files marked System and PC
Checker's own folders cannot be ticked, nor can any folder that contains one
of them. Junctions and symbolic links are never followed, and each item is
checked again right before it moves. Every move is written to the Action
Journal; no restore point is made because the Recycle Bin is the undo. The
space comes back when the Recycle Bin is emptied (Cleanup page).

### Debloat

Turns off ads, data collection, AI features and clutter, and removes
pre-installed apps, with the coverage of the popular debloat tools (WinUtil,
Win11Debloat, O&O ShutUp10++, Sophia Script and others; full list and table in
[DEBLOAT.md](DEBLOAT.md)).

- **Presets**: Recommended (highlighted), Gaming, Privacy max, Minimal UI,
  Remove consumer apps and Custom. A preset only ticks boxes.
- **Categories**: Privacy & telemetry, Ads & suggestions, AI & Copilot,
  Taskbar & Start, File Explorer, Windows preferences, Edge & OneDrive, Other
  apps (Office, Outlook, Chrome, Brave, Firefox, developer tools, NVIDIA
  Control Panel; shown only when installed), Gaming & Xbox, Automatic installs
  (WPBT firmware installers, device co-installers, remote Store installs), and
  (**Advanced**) Windows Update and Background services, plus AMD & NVIDIA
  software (only when AMD Software or NVIDIA's driver tools are installed:
  AMD User Experience Program, update and AMD Link tasks, Instant Replay and
  tray autostart, Crash Defender, NVIDIA telemetry) and Apps (consumer,
  promotional, retired Microsoft and PC-maker apps; AMD, NVIDIA, Intel and
  audio driver control panels are never offered). Each row
  shows its state (*Applied*, *Not applied*, *Partly applied*, *Not
  available* with the reason), a risk badge, *One-way* / *Explorer restart* /
  *Windows 11* badges, a warning when something stops working, and which other
  tools offer it. Hover a row for the exact registry values, services and tasks.
- **Apply selected** / **Revert selected** in the bar at the bottom, or per
  row. The confirmation lists every change grouped by risk. A restore point is
  always created first and previous values are saved, so *Revert* puts back
  exactly what was there (or the Windows default, which it says). When
  Windows cannot make the restore point, the page asks whether to turn on
  System Protection, continue without one (reversible tweaks only) or cancel.
- **Results** are read back from the PC after every batch: *Applied*,
  *Already applied*, *Skipped* or *Failed*, each with a plain reason, and
  applied changes say whether they show after a File Explorer restart, a
  sign-out or a Windows restart. Per-user tweaks go to the account signed in
  to Windows, even when PC Checker runs as another administrator.
- **Restart Explorer** (this session only, not elevated) or **Restart later**
  is offered after taskbar, Start, File Explorer and context menu changes; a
  sign-out note and a restart bar appear when those are needed.
- **Import / Export** the selection as JSON, including WinUtil exports.
- **Check these yourself** links to settings with no safe policy, and
  **Deliberately not offered** explains why disabling Defender or Windows
  Update, removing Edge or the Store, forcing S3 sleep and similar steps are
  left out.

### Right-click menu

The entries programs add to File Explorer's right-click menu (Clean up →
Right-click menu), and a way to turn off the ones you never use without
deleting anything.

- **What is listed**: commands (shell verbs) and shell extensions
  (ContextMenuHandlers) for files, files and folders, folders, inside a
  folder, drives and the desktop, plus the 20 most common file types
  (`.txt`, `.pdf`, pictures, music, video, Office, archives, `.exe`),
  including the program classes those types are linked to (the verb a
  double-click runs is left out so files always open); the
  items in **Send to**; and the Windows 11 entries of packaged apps (read
  from their manifests). Each line shows the text as Explorer shows it
  (resource strings such as `@shell32.dll,-8506` are resolved), the program
  and publisher, where it appears, its kind and whether it is on. Entries
  whose program is missing are marked *Not working*.
- **Turn off / Turn on** (Del and Ctrl+E, the right-click menu, or the
  buttons under the list) after a confirmation that says what changes.
  Commands get a `LegacyDisable` value in their own key; shell extensions and
  Windows 11 app entries are added to the current user's Shell Extensions
  *Blocked* list (turning on removes them from every Blocked list that holds
  them); Send to shortcuts move to a hidden backup folder in PC Checker's data
  folder and back. Ctrl+Z undoes the last change made on the page.
- **Windows' own entries** (files in the Windows folder, signed with
  Windows' certificate, or known built-in verbs and handlers) are hidden
  unless *Show Windows entries* is ticked, and then read-only. Entries from
  Microsoft apps outside the Windows folder (OneDrive, Office, Visual Studio
  Code, Notepad, Terminal) can be changed in Advanced mode. Graphics driver
  extensions in the driver store count as the hardware maker's.
- **Show the full (classic) right-click menu** on Windows 11: the per-user
  switch that removes *Show more options*, and back.
- **Restart File Explorer** is offered after a change, since Explorer reads
  its menu when it starts.
- Search, filters (on, off, files, folders, desktop and drives, Send to,
  Windows 11 apps, not working), *Open file location*, *Copy where it is
  registered* and *Search the name online*.

Every change asks first, goes through the change guard (restore point per
your settings, Action Journal entry with how to undo it) and is reversible.

### Registry

- **Honest intro**: registry "errors" rarely slow Windows down; the page
  removes leftovers that point at things that no longer exist, so lists stay
  tidy and some error dialogs or broken menu entries go away.
- **Categories** with a description, where they look and a risk badge:
  leftover uninstall entries, App Paths, shared file counters, file
  associations and Open with entries, startup entries, MUI cache, fonts, help
  files, services with a missing program (report only) and, in advanced
  mode, orphaned COM components, installer folder records and right-click
  menu handlers.
- **Scan** is read only and cancellable. Results are grouped per category
  with a checkbox (fixable entries only), the problem in plain English, the
  key path (copy button) and the missing file or folder.
- **Fix selected** asks first, creates a restore point, backs every entry up
  to a `.reg` file, checks each entry again and only then removes it.
- **Backups** lists every fix with *Restore*, *Delete* and *Open folder*;
  backups older than 90 days are offered for cleanup, never removed
  automatically. Details: [REGISTRY.md](REGISTRY.md).

---

## Security

### Security

A plain-language status board: one traffic light per protection (green good,
yellow worth a look, red fix this, grey could not be checked), what it is now,
what it means, and a button that opens the Windows setting (Windows Security,
Settings or the PC Checker page) or makes the safe fix.

- **Virus protection**: Microsoft Defender's real-time, tamper and
  cloud-delivered protection, definition version and age, last quick and full
  scan, engine and platform versions. When another antivirus is the active one
  (Windows Security Center), it is shown instead and Defender's scans are
  switched off on the page.
- **Firewall** per network type (work, home, public), or the third-party
  firewall in charge. *Turn on* switches a profile back on; turning the
  firewall off is never offered.
- **Windows version**: the version, edition and build, and the date Microsoft
  stops sending security fixes (a small table for Windows 10 22H2 and Windows
  11 21H2 to 25H2, Home/Pro and Enterprise/Education). Yellow 90 days before
  the end, red after; anything not in the table says to check Microsoft's
  lifecycle page.
- **Automatic updates** paused (links to the Windows Update page), **User
  Account Control** level (the four slider positions, off, or custom),
  **SmartScreen** for apps and files and in Edge, **Old file sharing (SMB
  1.0)** with *Turn off SMB 1.0* (restore point first, restart to finish),
  **Drive encryption** per fixed drive (BitLocker or device encryption, read
  only), **Secure Boot**, the **TPM** (present, ready, version), **Memory
  integrity**, **Credential Guard**, **Windows activation**, **Remote
  Desktop**, the **Guest account** and a waiting **Restart**.
- **Security score** out of 100: each check has a weight (antivirus and
  firewall most); a red item costs its full weight, a yellow one half, grey
  nothing. **Fix these first** lists the three biggest problems.
- **Microsoft Defender**: *Quick scan* (header), *Full scan* and *Update
  definitions* run Microsoft's own `MpCmdRun.exe` with a progress line and
  Cancel (which stops the scan); **Recent threats** lists what Defender found,
  how severe it was and what happened to it, with *Open Protection history*.

Everything is read through WMI and the registry using numbers and fixed names,
so it works the same on every Windows language. Checking never changes
anything. Fixes ask first and are written to the Action Journal; turning a
firewall profile on and SMB 1.0 off make a restore point first (per
Settings). Accounts with blank passwords cannot be checked without the
password and are skipped.

### Privacy

- **Camera, Microphone, Location**: every app Windows recorded using them
  (Store apps and desktop programs), when it last started, for how long, and
  *In use now* while it is using it. Each card shows the switch state (off for
  the whole device, off for all apps, desktop apps off, or on) and *Open
  settings* opens that Windows privacy page.
- **Turn off** stops one Store app from using the camera, microphone or
  location after you confirm: it sets that app's own switch to Deny, writes it
  to the Action Journal and offers **Undo**, which puts the previous value
  back. Desktop programs share one Windows switch, so they are changed in
  Windows Settings.
- **Windows privacy settings**: activity history, the advertising ID and the
  diagnostic data level at a glance. *Change* opens that tweak on the Debloat
  page, which saves the old value for Revert.

The records come from Windows' capability consent store
(`CapabilityAccessManager\ConsentStore` in HKCU and HKLM). When PC Checker
runs as a different administrator account, the page says whose records these
are.

---

### Backups
Every backup on the PC in one place (Security → Backups). The page only reads
until you press a button, and every change asks first and goes through the
change guard (Action Journal entry with how to undo it).
- **Hints** at the top, most urgent first: *System Protection is off on C:*,
  *No restore point in the last 30 days*, *Restore points use less than 1 GB
  — older ones will be deleted quickly* and *Your personal files are not
  backed up* (no File History and no OneDrive folder backup), each with the
  button that fixes it. The Dashboard's *What needs attention* card also says
  when System Protection is off on the Windows drive.
- **Restore points**: every System Restore point with its date, description,
  type in words (Windows Update, Device driver install, Checkpoint…) and who
  made it (Windows, Windows Update, an installer, PC Checker). *Create restore
  point* uses the same restore point service as the rest of the app, including
  *Allow more than one restore point a day* (also under Options here). Delete
  one point (Del or right-click; deleting the newest one asks a second time),
  or *Delete all restore points except the newest* from **More** (the same
  work as Cleanup's *Older restore points*). Only System Restore points are
  ever deleted; other shadow copies, such as those of backup software, are
  never touched.
- **System Protection** per fixed NTFS drive: on or off (read from Windows'
  own list of protected volumes, so it works in every language), the space
  restore points use and their limit, *Turn on* (with Undo for a few seconds)
  and *Up to N % of the drive* (vssadmin resize shadowstorage, with Undo; a
  limit below what is used now warns that Windows deletes the oldest points).
  *Turn off* is only shown in Advanced mode and says that the drive's restore
  points are deleted.
- **Windows backups**, read only with a button to open each: File History (set
  up or not), OneDrive folder backup (which of Desktop, Documents, Pictures,
  Music and Videos OneDrive keeps), Windows Backup, and *System image → Check
  now*, which asks wbadmin for system image backups only when you press it.
- **PC Checker's backups**: setup backups, registry backups, driver backups,
  scheduled task backups, hosts file backups and removed startup entries,
  each with its folder, count, size and newest date, *Open folder* and *Delete
  N old* (keeps the newest N, 5 by default, at least 1; moved to the Recycle
  Bin, never deleted for good). The debloat undo values and the Send to
  entries you turned off are shown too but never offered for deletion: they
  are needed to undo tweaks and to turn the entries back on.
## Performance

### Performance

- **Scan for slowdowns** looks at startup items, services, hungry processes,
  memory and commit pressure, page-file configuration, boot time, drive type
  and free space, TRIM, the power plan, fast startup, hibernation, visual
  effects, transparency, Game Mode, Game Bar, background apps, Storage Sense,
  SysMain, Windows Search, pending reboot, Windows Update age, uptime,
  thermal throttling and more.
- **Score** gauge and findings ordered by severity, each with the detail, the
  impact and a *Fix* button that runs the matching Optimize action (confirmed)
  or opens the right page.
- **Startup programs** with an enabled toggle, **Services** (**Advanced**)
  with a start-mode picker, **Top processes** by CPU and memory with *End
  task* (confirmed; system processes are protected), and a **System snapshot**.
- **Quick wins** card linking to Latency, Startup, Memory and Cleanup.

### Optimize

Reversible tweaks grouped by category, each row with its description, risk
badge, current state, *Apply* and *Revert*:

- **Safe**: power plan (Balanced, High performance), visual effects, Game
  Mode, Game Bar, background apps, Storage Sense, transparency, animations,
  focus notifications, TRIM, optimise drives, clean temporary files (opens
  Cleanup).
- **Moderate**: fast startup on/off, hibernation off.
- **From other pages**: the Latency fixes (HAGS, USB selective suspend, NIC
  power saving, PCIe ASPM, core parking, pointer precision, Game Mode),
  Memory (compression, SysMain, standby list, automatic page file), Startup
  (fast startup, boot timeout) and Keyboard & Mouse (pointer precision, gaming
  profile).
- **Advanced** (with a warning bar): Ultimate performance plan, Cloudflare
  DNS, minimal telemetry, SysMain off, Search indexing off, `DISM
  /RestoreHealth`, `sfc /scannow`, network reset, HPET off, dynamic tick off,
  Nagle off, MSI mode for the GPU. Long-running repairs stream their output.
- **Apply all safe recommendations** runs the safe set in sequence with
  progress.

### Benchmark

Six short tests score the PC against a reference machine, a mid-range 2024
desktop (6-core/12-thread processor, dual-channel DDR5-6000, PCIe 4.0 NVMe,
GeForce RTX 4060-class graphics), which scores about **1,000** on each test.
Nothing on the PC is changed.

- **Processor, single-core** and **multi-core**: Deflate compression of
  generated text, a prime sieve, a double-precision matrix multiply and
  SHA-256, on one thread and then on every logical processor. Each kernel
  checks its answer against the first one, so a processor that computes a
  wrong result (unstable overclock or undervolt) is called out.
- **Memory bandwidth**: several threads copy and write a large buffer
  (256 MB, less on PCs short of free memory). **Memory latency**: a pointer
  chase through a random single cycle of cache lines, so every load waits for
  memory.
- **System drive**: the Storage page's benchmark (sequential 1 MB and random
  4K, queue depth 1, unbuffered) on the Windows drive with a 256 MB temporary
  file that is deleted afterwards, also when cancelled.
- **Graphics compute**: the Overclock stress test's DirectX 11 compute load
  at full intensity without result checking, timed after a 3-second warm-up;
  throughput is reported in billions of shader steps per second. Skipped with
  the reason when there is no DirectX 11 card.
- Each timed test repeats its work for 12 seconds (10–20 in **Options**) and
  reports the median after dropping outliers (1.5 × the interquartile range);
  a note asks to run again when the repeats varied by more than 10 %. Scores
  are measured ÷ reference × 1,000 (inverted for latency); a test with several
  numbers and the **overall score** use the geometric mean.
- **Before a run** the page checks the power source, how busy the processor
  is (over one second; above 15 % is a warning) and CPU/GPU temperatures
  (80 °C or more). Warnings are shown in the confirmation and saved with the
  run, which shows them as a banner later.
- **Results**: summary tiles (overall score, change since the last run with
  the same tests, tests with a score, date) and one card per test with the
  score, a bar whose middle marker is the reference PC, the raw numbers, the
  number of repeats and their spread. **Run** on a card runs that test alone.
  **Cancel** keeps the tests that finished.
- **History**: every run is saved in `benchmarks.json` next to the settings
  (newest 100), with an optional note such as "Before overclock". **Show**
  puts an older run on the cards; **More → Remove this run** deletes one after
  confirming.
- **Compare two runs** side by side with the change in percent for each test
  and each raw value (changes under 3 % count as normal variation). The
  overall score is only compared when both runs completed the same tests, and
  notes say when the scoring, the hardware or the conditions differed.
- **More → Copy results / Save results as text / Copy comparison / Save
  comparison as text** produce plain text ready for a forum post.

The reference numbers are PC Checker's own estimates for its own workloads;
compare scores with other PC Checker runs, not with other benchmark apps.

### Overclock

Behind a one-time **consent gate** that explains the risks (instability,
crashes, data loss, heat, warranty), what the app can and cannot control on
this PC, and the safety features.

- **Supported hardware** matrix: feature × vendor × level (Full, Partial,
  Experimental, Monitor only, Unsupported) with a one-line reason for your
  machine, and vendor badges next to the GPU and CPU names.
- **GPU tuning** (when a method is available): core offset, memory offset,
  power limit, temperature limit and fan percent sliders with numeric boxes,
  ranges clamped by the driver's limits and your Settings. *Apply*
  (confirmed; lists the changes, the restore point and the watchdog), *Reset
  to defaults*, *Save as profile*, load profile, presets *Default*, *Mild*
  (+50/+200) and *Moderate* (+100/+500). Live clock, temperature, power and
  fan readouts.
  - **NVIDIA**: through NVAPI. Clock offsets are fully supported; power and
    temperature limits where the driver API exposes them (the page notes when
    it does not).
  - **AMD**: through ADL/Overdrive8, marked **experimental**. Every value is
    validated against the driver's own minimum, maximum and capability mask
    and read back after it is sent. RX 7000/9000 add a **voltage offset**
    (undervolt) slider and Radeon presets (*Undervolt -50 mV*, *Undervolt
    -80 mV + power -10 %*, *Quiet*); the core control is called *Max
    frequency* and the minimum clock is kept 100 MHz below a lowered maximum.
    The GPU fan goes through an Overdrive8 fan curve (RX 5000 and newer
    ignore the hardware monitor's fan commands). *Reset* only touches what
    PC Checker manages and retries one setting at a time. If any call fails
    the page falls back to *Open AMD Software: Adrenalin Edition*.
  - **Intel Arc**: through IGCL, marked **experimental**; integrated Intel
    graphics are monitor only. Fallback: *Open Intel Graphics Software*.
- **CPU**: Intel XTU CLI when installed (lists the tunables it recognises and
  refuses to guess when a name does not match); AMD Ryzen Master launch
  (found through its uninstall entry; notes on laptops and Virtualization-Based
  Security), and `ryzenadj` power limits on Ryzen laptops, handhelds and APUs
  only (5 W and up, short boost 25 % above the sustained limit, re-applied
  every 45 seconds, a blocked WinRing0 driver is explained). Otherwise BIOS
  guidance that follows the Ryzen model (5000X3D locked, 7000X3D PBO and
  Curve Optimizer only, where each board maker hides the menu) and a download
  link.
- **Memory**: XMP/EXPO state and a link to the Memory page.
- **Stress test**: target (CPU, GPU, both), duration, abort temperatures,
  live readouts (elapsed time, GPU work rate, errors, temperatures and
  clocks), temperature sparklines, and a verdict with plain-English reasons
  (errors, peak temperatures, average clocks, throttling, driver resets). It
  needs no overclock consent: consent only gates applying clocks.
  - **CPU**: a built-in all-core workload that verifies its own results.
  - **GPU, built in**: a vendor-neutral DirectX 11 compute workload, so no
    game is needed (NVIDIA, AMD and Intel, integrated graphics included).
    Pick the graphics card when the PC has more than one (the one with the
    most video memory is recommended) and an intensity: *Light* (small
    buffer, about half-time load), *Medium* (near-constant load, like a
    demanding game) or *Heavy* (back-to-back work over up to 1 GB of video
    memory, capped at a quarter of the card's memory). The work per pass is
    calibrated so each pass takes 50 to 100 ms, far below the Windows driver
    timeout. With *Check results for errors* on, part of the result is read
    back every couple of seconds and compared with the first run; any
    difference counts as an error, the same kind of fault that shows up as
    artifacts in games. If the graphics driver resets, the test stops,
    reports "The graphics driver reset — the GPU is not stable at these
    settings" and resets an applied overclock to defaults.
  - **GPU, guided**: tick *Use my own game or benchmark instead* and the app
    watches temperatures and clocks while your own load runs.
  - **Safety**: the watchdog samples temperatures every second and stops at
    your limits (resetting an applied overclock); a laptop on battery below
    20 % cannot start a test and above that gets a warning; *Stop* ends
    everything within a second.
- **Claude advisor** (when configured): describe your goal and get a staged
  plan with warnings and assumptions; *Apply this step* loads the step into
  the sliders for you to confirm, *Run suggested stress test* runs it, *Send
  results back* asks for the next step. The disclaimer is always visible.
- **Watchdog**: a session marker is written when a profile is applied; if the
  PC did not shut down cleanly the next start resets the GPU and tells you.
  GPU offsets do not persist across reboots in any case.
- **Advanced**: raw limits, offsets up to the driver's range beyond your
  Settings maximum (still validated), and the session file.

### AMD platform support

AMD-specific behaviour across the app (CPU traits come from the processor
name: Zen generation, socket, desktop/laptop/APU, X3D, Tjmax):

- **The right GPU**: the integrated Radeon of every Ryzen 7000/9000 desktop
  is never taken for "the GPU" on the Dashboard, overlay, Overclock page,
  fan profiles, power total or watchdog; the discrete card is. One rule
  recognises current Radeon APUs (760M, 780M, 890M, 8060S, Vega 3–11).
- **Temperatures**: Tdie is shown instead of the offset Tctl on Ryzen
  1000/2000 X and Threadripper. With the stock thresholds, Ryzen 7000
  (except 7000X3D) and 9000 desktop chips, 9000X3D included (which run at
  95 °C by design), use 90/98 °C and the stress test stops at Tjmax + 3 °C;
  integrated Radeon sensors share the CPU's limits, and GPU hot spot
  (100/110 °C) and memory (95/105 °C) have their own. Integrated GPU power is
  not added to the CPU package twice.
- **X3D**: on dual-CCD Ryzen 9 X3D, nothing recommends High performance or
  turning core parking off, actions that would undo AMD's CCD parking (and
  game profiles that switch plans, removing Game Bar) warn first, and the
  Latency page checks Balanced, Game Mode, Game Bar and the V-Cache service.
- **Health**: AMD Chipset Software, PPM provisioning, platform devices on
  generic drivers and the 3D V-Cache optimizer; WHEA hardware errors with
  Ryzen advice (Curve Optimizer core, FCLK, PCIe, memory, BugCheck 0x124);
  graphics driver timeouts (Display 4101, LiveKernelEvent 141/117).
- **Memory**: EXPO is judged against the part-number rating as well, a JEDEC
  speed is "check the BIOS" rather than "on", and the Infinity Fabric 1:1
  sweet spot (AM5 DDR5-6000, AM4 DDR4-3600) and the AM5 training pause are
  explained.
- **Drivers**: older Adrenalin packages count as superseded, AMD chipset
  components are not "old", Windows Update display and chipset offers that
  would replace AMD Software are unticked with a warning and can be hidden,
  and the chipset link names your socket.
- **Latency**: AMD BIOS guidance (CPPC, Global C-states, AM4 Power Supply
  Idle Control) and the legacy *AMD Ryzen Balanced* plan.
- **Overlay**: a note that AMD Fluid Motion Frames are not counted.

---

## Gaming

### Game library

The **Games** tab lists every installed game from the launchers on the PC, read
from each launcher's own files and registry entries. No launcher is started to
find games, and nothing is changed while looking.

- **Steam**: the install folder from the registry, every library in
  `steamapps\libraryfolders.vdf` (old and new layouts), each
  `appmanifest_*.acf` for the name, size, last update and install state, and
  *last played* from each account's `userdata\<id>\config\localconfig.vdf`.
  Redistributables, Proton, the Linux runtimes and SteamVR are left out. Cover
  art comes from Steam's own `appcache\librarycache`.
- **Epic Games**: the launcher's `Manifests\*.item` files (add-ons and engines
  left out).
- **GOG Galaxy**: `HKLM\SOFTWARE\WOW6432Node\GOG.com\Games` (DLC left out).
- **EA app**: the *EA Games* and *Electronic Arts* registry keys and the
  Uninstall entries published by Electronic Arts, with the title and program
  from each game's `__Installer\installerdata.xml`.
- **Ubisoft Connect**: `HKLM\SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs`.
- **Battle.net**: Uninstall entries published by Blizzard (the launcher's own
  database is not readable).
- **Riot Client**: Uninstall entries that point at `RiotClientServices.exe`.
- **Xbox app and Microsoft Store**: installed packages with a
  `MicrosoftGame.config`, and the folders each drive's `.GamingRoot` file lists
  (`C:\XboxGames` by default).

Each game is a card with its cover (or its program's icon), launcher, size,
drive and when it was last played. Sort by name, size or last played; filter by
launcher and drive; search. The tiles show how many games there are, the space
they use and the game played last, with the space per drive below. Sizes a
launcher does not record are measured in the background.

Per game (buttons, the card's **More** button and its right-click menu):

- **Play** starts the game through its launcher's link (`steam://rungameid/…`,
  `com.epicgames.launcher://…`, `uplay://…`, `shell:AppsFolder\…`) or its
  program, with your normal rights, not PC Checker's administrator rights.
- **Open folder**.
- **Verify files** asks the launcher to check and repair the game (Steam and
  Epic directly; GOG Galaxy, the EA app, Ubisoft Connect, Battle.net and Xbox
  open the right place and say where to click). Asks first.
- **Uninstall** always goes through the launcher (Steam, Epic, GOG Galaxy,
  Xbox) or the Apps page (EA, Ubisoft, Battle.net, Riot). PC Checker never
  deletes a game's folder itself. Asks first and is written to the Action
  Journal.
- **Scan for missing DLLs** opens the Missing DLLs page and scans the game's
  folder.
- **Game profile…** opens the Overlay page's profile editor with the game's
  program filled in (or its existing profile).
- **Clean shader cache** (Steam games): moves `steamapps\shadercache\<appid>`,
  and nothing else, to the Recycle Bin after asking. Steam builds it again.
  Journaled; restore it from the Recycle Bin.

Limitations: only Steam records when a game was last played. Battle.net game
pages open only for the major titles whose launcher code is known; others open
the Battle.net app. Games on a drive that is unplugged are not listed.

### Overlay

Configures the in-game overlay; the overlay itself is a separate click-through
window. Full detail in [OVERLAY.md](OVERLAY.md).

- **Enable** toggle, **Show / Hide now**, **Edit position on screen** (makes
  the overlay draggable; drop saves a custom position).
- **Hotkey** (default `Ctrl+Shift+O`, validated as you type).
- **Corner** or custom X/Y, **opacity**, **scale**, font family and size,
  **orientation** (vertical list or horizontal row), background on/off and
  colours (background, text, accent), **sparklines**, **update interval**.
- **Metrics** list with checkboxes, reorder buttons and editable labels.
- **Presets**: save, load and delete; built-ins *Minimal*, *Full* and *Temps
  only*.
- A live preview on a sample "game" background, naming the game the FPS
  metrics are measured for.
- **FPS counter**: metrics *FPS*, *Frame time* and *1% low* for the game in
  front, counted from the Windows graphics present events like PresentMon
  (needs administrator rights, which the app has). The card explains why it is
  unavailable when Windows refuses the tracing session.
- **Game profiles**: per game (matched by its .exe name) an overlay preset and
  visibility, a fan profile and a power plan. Applied after the game has been
  in front for 2 seconds, put back when the game's process exits. Add from the
  running programs or by browsing for the .exe; edit, remove, switch on/off;
  global *Switch automatically* and *Only when the game fills the screen*; a
  status line shows the active profile. Switches are journaled (module
  *Gaming*).

Limitations: the overlay is a transparent desktop window. It shows over
borderless and windowed games; exclusive full-screen games hide every desktop
window. Some anti-cheat systems block overlays and can hide a game's frames
from the FPS counter (it then shows "--").

### Latency

- **Run latency check** with a sample length (5, 10 or 30 seconds) and an
  *include network* toggle.
- **Score** and tiles: DPC time %, interrupt time %, DPCs per second,
  interrupts per second, timer resolution (current and minimum), mouse polling
  Hz and DNS lookup time.
- **Network table**: your gateway, 1.1.1.1, 8.8.8.8 and any targets you add
  in Settings, with average, minimum, maximum, jitter and loss and coloured
  badges.
- **Findings** with *Fix* buttons: HAGS, USB selective suspend, NIC power
  saving, PCIe ASPM, core parking, pointer precision, Game Mode, the power
  plan, and in **Advanced** HPET, dynamic tick, Nagle and MSI mode (each with
  an honest note on when it helps). Timer resolution is explained as
  information only because Windows 11 manages it per process.
- **Learn** expander: what DPC and ISR mean, what good values look like, and
  that real-time DPC tracing needs LatencyMon or Windows Performance
  Analyzer (linked).
- **History** of the last five runs.

Limitations: the performance counters can be corrupt on some systems; the
page shows the `lodctr /R` hint. Ping results depend on your network at that
moment.

### Missing DLLs

Old games often stop with "d3dx9_43.dll was not found" or "MSVCP140.dll is
missing". The fix is never a single DLL file from a "DLL download" website:
those files are unsigned, often the wrong version or architecture, and
sometimes malware. Every DLL on this page is mapped to the official package
that contains it, and *Install* installs that package from its maker.

- **The list**: about 240 DLLs games ask for, each with its 32-bit (SysWOW64)
  and 64-bit (System32) state and the package it comes with. Search it,
  filter it by package, or show *Missing only*. Detection reads the real
  folders (PC Checker is 64-bit, so nothing depends on file-system
  redirection), the side-by-side store for Visual C++ 2005 and 2008, the
  .NET 2.0 and XNA folders, the uninstall entries and Microsoft's Visual C++
  runtime key.
- **Packages** with their state (installed, partly missing, missing) and the
  installed versions:

  | Package | DLLs (examples) | Installed with | Source and signer |
  |---|---|---|---|
  | DirectX End-User Runtime (June 2010) | d3dx9_24–43, d3dx10, d3dx10_33–43, d3dx11_42/43, d3dcompiler_33–43, d3dcsx_42/43, xinput1_1–1_3, x3daudio1_0–1_7, xapofx1_0–1_5, xactengine2_0–2_10 and 3_0–3_7, xaudio2_0–2_7 | `directx_Jun2010_redist.exe /Q /T:<folder>`, then the signed `DXSETUP.exe /silent` | download.microsoft.com, Microsoft Corporation |
  | Visual C++ 2015–2022 (v14), 32 and 64-bit | vcruntime140, vcruntime140_1 (64-bit only), msvcp140 and its _1, _2, _atomic_wait, _codecvt_ids parts, concrt140, vccorlib140, vcomp140, mfc140 | winget `Microsoft.VCRedist.2015+.x64/.x86`, else `vc_redist.<arch>.exe /install /quiet /norestart` | aka.ms → download.visualstudio.microsoft.com, Microsoft Corporation |
  | Visual C++ 2013, 2012 | msvcr120/msvcp120, msvcr110/msvcp110 and their MFC, OpenMP and C++/CX parts | winget, else `/install /quiet /norestart` | aka.ms / download.microsoft.com, Microsoft Corporation |
  | Visual C++ 2010 | msvcr100, msvcp100, mfc100 | winget, else `/q /norestart` | download.microsoft.com, Microsoft Corporation |
  | Visual C++ 2008, 2005 | msvcr90/msvcp90, msvcr80/msvcp80 (side-by-side) | winget, else `/q` | download.microsoft.com, Microsoft Corporation |
  | .NET Framework 3.5 | mscorwks (the 2.0 runtime) | Turns on the Windows feature NetFx3 | Windows Update |
  | DirectPlay | dplayx, dpnet, dpwsockx… | Turns on the Windows feature DirectPlay | Windows |
  | XNA Framework 4.0 | Microsoft.Xna.Framework*.dll | `msiexec /i xnafx40_redist.msi /qn` | download.microsoft.com, Microsoft Corporation |
  | OpenAL | OpenAL32, wrap_oal | `oalinst.exe /s` from Creative's `oalinst.zip` | www.openal.org, Creative Labs Inc |
  | NVIDIA PhysX System Software and Legacy | PhysXLoader, PhysXCore, PhysXDevice, cudart32_30_9; NxCooking, NxCharacter | winget `Nvidia.PhysX` or `/s`; the legacy MSI with `/qn` | us.download.nvidia.com, NVIDIA Corporation |

  Not installable, and the page says why: Games for Windows Live (`xlive.dll`;
  the service closed, use the game's own patch or store version), the Visual
  C++ 2003 runtime (`msvcr71.dll`; Microsoft no longer offers it, reinstall
  the game), the Media Feature Pack on Windows N editions (opens Settings →
  Optional features), Vulkan (`vulkan-1.dll`, comes with the graphics driver;
  opens the Drivers page), and DLLs that are part of Windows such as
  `d3d9.dll`, `ddraw.dll`, `d3d8.dll`, `xinput1_4.dll` and `msvbvm60.dll`.
- **Scan a game…**: pick a game's folder or its .exe. PC Checker reads the
  programs at the top level and one folder down (at most 300 files), works
  out from each file's header whether it is 32 or 64-bit and reads its
  import and delay-import tables. Nothing in the folder is run. Each DLL is
  then: included with the game, part of Windows (including the
  `api-ms-win-*` API sets), installed, missing with the package that fixes
  it, or *not found* (usually part of the game itself: verify the game files
  in its launcher). *Install what's needed* installs each package once, for
  the right architecture.
- **Install everything missing** lists every missing package first.

Every install asks first and shows the package, the server it comes from and
who must have signed it. Downloads use HTTPS from the allow-listed vendor
servers only (every redirect is checked too), go to a fresh folder only
administrators can change, and are deleted unrun unless Windows accepts the
Authenticode signature and the signer is the expected company. Installs are
written to the Action Journal. Runtime installers make no restore point (they
add files next to Windows' own and are removed in Installed apps); turning on
.NET 3.5 or DirectPlay follows the Windows Features page's rules. Exit codes are explained: 3010
asks for a restart, 1638 means a newer version is already there, and 5100
usually means a newer Visual C++ runtime is installed. The list is checked
again afterwards. PC Checker never copies a DLL into System32 or SysWOW64.

Limitations: a .NET game (such as an XNA game) shows only `mscoree.dll` in a
scan, so its XNA or .NET needs are not detected from the files; the packages
section still shows them. Creative does not document OpenAL's `/s` switch
(it is the one Steam uses). OpenAL's installer is signed with an older
certificate; if Windows no longer accepts it, PC Checker refuses to run it and
the vendor page button opens openal.org instead.

---

## Network

### Network

- **Connection summary**: active adapter, IP address, router and DNS servers,
  link speed and Wi-Fi signal.
- **Wi-Fi**: network name, band and channel, a signal gauge with the dBm
  estimate, Wi-Fi standard, link rates and security, read from `netsh wlan`
  by the shape of the output so localized Windows works. Findings in plain
  English: weak or fair signal, crowded 2.4 GHz channel (with the least
  crowded of 1/6/11), the same network available on 5/6 GHz, a link rate too
  low for the standard, open/WEP/TKIP security, and Wi-Fi power saving (one
  click fix). Windows 11 24H2 needs Location permission for the scan; the
  page explains it and opens the right Settings page.
- **Nearby networks** table and **channel congestion** bars per band.
- **Speed test** (HTTP, `speed.cloudflare.com` by default): idle ping,
  download and upload over four connections, ping under load and the
  bufferbloat grade (A+ to F) with an explanation, data used, live progress
  and Cancel. Not run automatically because it uses data.
- **Packet loss test** (50, 100 or 500 pings to the router, 1.1.1.1 and
  8.8.8.8): loss, average, jitter and 95th percentile per target, plus where
  the loss starts (home network or provider).
- **Tune Wi-Fi and network**: Wi-Fi power saving, "allow the computer to
  turn off this device", TCP auto-tuning, ECN, RSS, a fast public DNS
  provider and, in Advanced mode, DNS over HTTPS and prefer IPv4. Adapter
  driver settings (preferred band, roaming aggressiveness, MIMO power save,
  transmit power, wireless mode, channel width, Energy-Efficient Ethernet,
  speed and duplex) with curated recommendations. Saved Wi-Fi networks:
  order, auto-connect, metered, forget (backed up first). Every change asks
  first, goes through the change guard and has **Revert**, also after a
  restart.
- **Quick improve Wi-Fi**: a checklist of the recommended changes that differ
  from the current state, applied one by one, then a re-test with the
  results before and after side by side.
- **Better Wi-Fi without software**: router placement, band trade-offs, the
  best channel to set on the router, firmware, mesh or Ethernet.
- **Adapters** table with errors and discards, and **fix tools**: flush DNS,
  renew IP, reset network stack (Advanced), Wi-Fi and network settings.

Limitations: the router's channel and QoS can only be changed on the router;
the nearby list is Windows' latest scan; speed results depend on the server
and the time of day. See [NETWORK.md](NETWORK.md).

### Connections

A TCPView-style live list of every open TCP and UDP endpoint (IPv4 and
IPv6) with the program behind it, on the *Connections* tab of the Network
section.

- **Programs** on the left with their icon, process ids and connection
  count (every Chrome process is one program); pick one to see only its
  connections, or *All programs*.
- **Connections** on the right: program, PID, protocol, local and remote
  address and port, state in plain words (Listening, Connected, Recently
  closed…, with the technical name as a tooltip) and, with *Show host
  names* on, the remote host name (reverse DNS through your own DNS server,
  looked up in the background and remembered; off by default).
- **Filters**: all, listening (TCP listeners and UDP sockets) or connected; *Hide this-PC-only* (on by
  default) hides traffic between programs on the PC itself; a search over
  program, address, port, state and host name.
- **Open to other devices**: a program listening on every network adapter
  (0.0.0.0 or ::) on a port Windows does not open itself (TCP, or UDP below
  Windows' outgoing port range 49152) is marked
  "Accepts connections from other devices", counted in a tile and listed in
  a banner with its ports.
- Refreshes every 2 seconds while the page is visible, with *Pause*
  (Ctrl+P) and *Refresh* (F5).
- **Actions**: *Copy* (Ctrl+C), *Search the remote address online*, *Open
  file location* (Ctrl+O), *Go to the process* on the Processes page
  (Ctrl+G), *Close connection* (Del; IPv4 TCP only, administrator, confirmed
  and journaled; the program may reconnect) and *End process* (Shift+Del;
  through the Processes page's service with all its protections).

No geo-IP or other outside service is used; the country of an address is
not shown.

### Hosts file
The Windows hosts file (Network → Hosts file), found through `%SystemRoot%`
and the Tcpip `DataBasePath` setting, as a table and as text.
- **Table**: address, host names, note and a tick box per entry, with search
  and filters (on, off, blocked sites, possibly unwanted). Add (Ctrl+N), edit
  (Enter or double-click), turn on or off (Space) and remove (Del); the add
  box has *Block* (0.0.0.0) and *This PC* (127.0.0.1) buttons, cuts pasted
  web addresses down to the name and explains invalid input such as
  wildcards. Comments, blank lines and lines Windows cannot use are kept
  exactly as they are; only edited lines are rewritten.
- **Text** view of the whole file for people who prefer to type.
- **Possibly unwanted (malware does this)**: entries that send well-known
  sites (Microsoft, Google, Apple, social networks, shops, game stores) or
  bank and payment sites to another address, and entries that block Windows
  Update, Microsoft Defender, SmartScreen or security vendors' update sites.
  Ordinary blocking of ads, trackers and telemetry is not flagged.
- **File checks**: read-only, hidden or system attributes, a file over 1 MB,
  a missing file, and Windows reading the file from an unusual folder.
- **Save** (Ctrl+S) asks first with a summary of what changed, refuses when
  another program changed the file since it was read, keeps a timestamped
  copy in `%LOCALAPPDATA%\PcChecker\backup\hosts`, lifts read-only only
  while writing, keeps the encoding (UTF-8 with or without BOM, ANSI, UTF-16)
  and line endings, goes through the change guard and clears the DNS cache.
  **Undo last save**, a **Backups** list with *Restore*, and **Reset to
  Windows default** (Microsoft's default text, with a backup).
- When security software locks the file, the page says so and how to save
  anyway (pause its hosts file protection for a moment).

---

## Ask Claude

### Ask Claude

Optional; nothing is sent until you add an API key and press send.

- **Not configured** state: what the feature does, what is sent, the cost
  note, *Get an API key*, a key box with *Save*, model and effort pickers, a
  *Share scan context* toggle and *Test connection*.
- **Chat**: messages with *Copy*, an input box (Enter sends, Shift+Enter
  newline), *Send* / *Stop*, suggested questions ("Why is my PC slow?", "Is my
  disk healthy?", "What should I update first?", "Explain my health report"),
  a *Context being shared* expander that shows the exact summary text that
  goes with your message, and *Clear chat*.
- Other pages can hand it a question (*Explain with Claude* on Health).

Details, privacy and troubleshooting in
[CLAUDE-INTEGRATION.md](CLAUDE-INTEGRATION.md).

---

## Footer

### Logs

- **Live log**: level and category filters, search, pause, clear, copy, *Open
  log folder*, *Export diagnostics* (a zip of the logs, the journal, settings
  with the API key removed and a system summary).
- **Logging settings**: minimum level, per-category overrides, file logging,
  folder, rolling interval, size limit, files kept, JSON format, output
  template, in-app buffer, redact serial numbers, log sensor samples.
- **Action Journal**: every change the app made with time, module, action,
  target, outcome, restore point number, before and after values and the undo
  hint; *Open journal file* and *Open System Restore*.

Details in [LOGGING.md](LOGGING.md).

### Settings

Sections with immediate save: General (theme, advanced mode, start minimized,
check health on startup, *Install in Program Files* while running from
another folder), Tray and Schedules (see the next two sections),
Updates (version, channel, automatic checks, what happens when a new
version is out, *Check for updates*, *Download*, *Install and restart*,
release notes, and the skipped version with *Offer it again*), Restore points (before, after, bypass frequency limit,
and the restore-point list with *Create now*, *Enable System Protection* and
*Open System Restore*), Monitoring thresholds, Electricity price, Fans
(revert on exit, minimum percent), Lighting (OpenRGB host and port,
auto-connect), Latency (ping targets and count), Overclock limits (GPU core
and memory offset, highest GPU power limit, highest GPU and CPU
temperature), Memory and Startup warnings, Claude, Data and privacy
(open settings and log folders, reset settings, export diagnostics) and
About (version, licences, GitHub link, *Show welcome again*). A note shows when the app is running
in demo mode. Overlay options are on the Overlay page, logging options on the
Logs page and cleanup categories on the Cleanup page.

### Your setup (backup and restore)

**Settings → General → Your setup** saves everything you set up in PC Checker
to one `.pcchecker` file and brings it back on this PC after a reinstall, or on
a new one. *Back up your setup* and *Restore your setup from a file* are also
quick actions (Ctrl+K).

- **What the file holds**: every settings section of `settings.json` (theme,
  language, alerts and limits, restore points, updates, logging, cleanup,
  network and latency tests, history recording), fan profiles and curves,
  lighting profiles, the overlay layout and presets, game profiles, display
  profiles, the memory warning (the memory profiles themselves are built in),
  keyboard and mouse profiles, overclock limits and profiles, schedules and
  tray options, and, if you tick it, the benchmark history from
  `benchmarks.json`. The data looks exactly like the matching part of
  `settings.json`, inside a small versioned envelope (`Format`,
  `SchemaVersion`, and a `Version` per part).
- **Never included**: the Claude API key (Windows protects it with DPAPI for
  this account on this PC only, so it would not work elsewhere anyway; the
  dialog says so), the recorded History data and logs, and this PC's own state:
  window size, recent searches, the welcome flag, the log folder, the active
  fan, lighting and overclock profiles and the overclock consent. An import
  never takes these from a file either, even a hand-edited one.
- **Restore**: pick a file and PC Checker checks it first: is it JSON, is it a
  setup file, which version. Parts made by a newer version, parts it does not
  know and parts whose values are not in the expected form are listed as
  skipped with the reason; the rest can still be imported. A plain
  `settings.json` (for example a copy you kept, or the `settings.corrupt-*.bak`
  the app sets aside) is read as an older layout and converted. The preview
  shows each part with a tick box and what it would change ("General › Theme:
  System → Dark", "Profiles "Silent": added", "5 runs in the file, 3 new"),
  and parts that already match are left unticked. Overclock limits and
  profiles are never ticked by default.
- **How values are applied**: for each ticked part the file's values replace
  this PC's field by field; a field the file does not have keeps its current
  value. Values are clamped to the ranges the Settings page allows. Empty
  entries and unnamed profiles in a hand-edited file are left out, and empty
  text gets its default, so a damaged file cannot break a page. A shorter
  history retention gets the usual 24-hour grace period, so importing never
  deletes recorded history straight away. Benchmark runs are added (runs you
  already have are kept). The history holds the newest 100 runs, so if new
  runs would go over that the preview says how many of your oldest runs would
  be removed, and *Undo* puts them back. Imported overclock profiles are still checked
  against your limits and the card's own range when you apply one; nothing is
  applied to hardware by an import.
- **Schedules**: importing *Schedules and tray* creates, updates or removes
  PC Checker's Windows scheduled tasks and the *Start with Windows* task to
  match, through the same guarded path as the Schedules switches. A task that
  cannot be created is left as it was and the import says so. Import, reset
  and undo all list the task changes and ask before touching Task Scheduler.
  If the settings then cannot be saved, the tasks are put back as they were.
- **Backup first, undo after**: before every import (and every reset) the
  current setup, benchmark history included, is saved to
  `%LOCALAPPDATA%\PcChecker\backups\setup-<time>.pcchecker`. If that fails,
  nothing is changed. *Undo* puts the changed parts back from that backup and
  removes the benchmark runs the import added (and restores any it pushed
  out). Older backups stay in the folder (*Open backups folder*) and can be
  restored like any other file. The app never deletes them, so the folder
  grows by one full backup (benchmark history included) per import or reset;
  delete old ones by hand when you no longer need them.
- **Reset one part**: tick parts, such as only fans or only the overlay, to
  put them back to their defaults, with the same automatic backup and undo.
  The API key is kept.
- Every import, reset and undo is written to the Action Journal with the
  backup's path. Open pages (Fans, Lighting, Overlay, Displays, Keyboard &
  Mouse, Overclock, Benchmark) reload their lists after an import. In demo
  mode the automatic backups are kept in memory and two sample ones are listed.

### Tray

PC Checker keeps an icon in the notification area next to the clock.

- **Tooltip**: hover the icon for "PC Checker — CPU 62 °C · GPU 58 °C",
  refreshed every 2 seconds, plus the number of active alerts.
- **Popup** (left-click): a compact, rounded panel next to the tray with CPU,
  GPU and hottest-drive temperatures coloured by your Monitoring thresholds,
  CPU and GPU load, RAM use, estimated power draw, the alert count, a fan
  profile quick switch, the overlay on/off switch and the buttons *Open PC
  Checker*, *Quick clean* and *Exit*. It hides when you click elsewhere.
- **Menu** (right-click): *Open PC Checker*, *Show overlay*, *Fan profile ▸*,
  *Check health now* and *Exit PC Checker*.
- **Quick clean** deletes temporary files from the Safe cleanup categories you
  use on the Cleanup page, after you confirm. Files changed recently (the
  "skip files newer than" Cleanup setting, an hour by default), files in use and the Recycle Bin are left alone. The
  result arrives as a notification.
- **Fan profile switch** applies a built-in or saved profile; the first switch
  in a session asks for the same consent as the Fans page.
- **Exit** from the tray runs the normal shutdown: fans go back to automatic
  (when that option is on), the overclock session is marked clean and the logs
  are flushed.

Settings → Tray:

- *Show the PC Checker icon in the tray* (on by default). While *Minimize to
  the tray* or *Close button keeps PC Checker in the tray* is on, the icon
  stays so you can get back to the window; the setting says so.
- *Minimize to the tray*: the minimize button hides the window; monitoring,
  alerts and the overlay keep running Together with *Start minimized*, PC
  Checker starts hidden in the tray.
- *Close button keeps PC Checker in the tray*: closing hides the window. Exit
  from the tray menu to quit.
- *Start with Windows*: adds the task `PC Checker\Start` to Task Scheduler
  (`schtasks /Create /XML`: at your sign-in, 30 seconds later, highest
  privileges). Because the task has highest privileges, the app starts
  without a UAC prompt. Unlike Task Scheduler's defaults, the task also starts
  on battery power and has no time limit, so a laptop on battery still starts
  PC Checker and Windows never ends a long-running session (which would skip
  the normal shutdown that puts fans back to automatic). Turning it off
  removes the task.
- *When started with Windows, stay in the tray*: the task starts
  `PcChecker.exe --tray`, which opens with only the tray icon.
- *Show the temperature as the tray icon*: the icon becomes the hotter of the
  CPU and GPU in °C, white below your warning level, yellow at warning and red
  at critical.
- *Temperature alerts as notifications*: while the window is hidden or in the
  background, a temperature alert shows as a Windows notification, at most one
  per part every 10 minutes. Clicking it opens Temperatures.

Starting `PcChecker.exe` a second time brings the running copy to the front,
even when it is hidden in the tray.

### Schedules

Settings → Schedules runs four maintenance jobs on a timetable through Windows
Task Scheduler, so they run even while PC Checker is closed:

| Job | What it does | Needs your attention when |
|---|---|---|
| Health check | Runs the full health check and keeps the score and critical items. | The score dropped 5 points or more since the last run, or a new critical problem appeared (on the first run: score below 70 or any critical item). |
| Junk file cleanup | Scans and cleans the **Safe** categories you use on the Cleanup page. Never Moderate or Advanced categories, never the Recycle Bin, never files changed recently (the Cleanup setting, an hour by default). Optional dry run that only counts. | A file could not be removed. |
| Driver update check | Asks Windows Update for newer drivers. Nothing is downloaded or installed. | Updates are available. |
| PC report | Saves a full HTML report (hardware, health, performance, drivers) to `Documents\PC Checker Reports` and keeps the newest N scheduled reports (10 by default). Reports you saved yourself are never removed. | Never. With a notification after every run, clicking it opens the report. |

For each job: an on/off switch, daily or weekly (with the day), the time
(24-hour), the job's options, the next run, the task's state in Task Scheduler
(including its last result in plain words), the last result with when it ran
and how long it took, and *Run now* (*Clean now* for cleanup, which asks
first). Turning a job on explains what will happen and creates the task;
changing the time or day shows *Save*, which updates it; turning it off
removes the task.

How it works:

- One task per job in the `PC Checker` folder of Task Scheduler, registered
  with `schtasks /Create /TN "PC Checker\<Job>" /XML <definition> /F`. The
  definition runs `"<path to PcChecker.exe>" --scheduled <job>` daily or
  weekly at your time with highest privileges, also on battery power (Task
  Scheduler's defaults would skip a laptop's runs on battery), with a one-hour
  hard stop behind the job's own 10-minute limit.
- The definition file for this and for *Start with Windows* is written to a
  new folder under `%ProgramData%` that only Administrators and SYSTEM can
  change, and is deleted straight after. It never goes through your `%TEMP%`
  folder, where another program running as you could swap it for its own
  command before Task Scheduler reads it.
- A task (a schedule or *Start with Windows*) is only created when
  `PcChecker.exe`, its folder and every folder above it can be changed by
  Administrators, SYSTEM and TrustedInstaller only, as under `Program Files`.
  From a folder such as Downloads or the Desktop, another program running as
  you could replace `PcChecker.exe` and be started with administrator rights
  without a UAC prompt, so PC Checker asks you to move it to
  `C:\Program Files\PC Checker` first (*Install in Program Files* in
  Settings → General does this).
- When PC Checker runs as a different administrator account than the person
  signed in (a standard account that typed an administrator's password at the
  UAC prompt), no task is created: Task Scheduler would only start it while
  that administrator is signed in, never for you.
  Status is read with `schtasks /Query /FO CSV /V /NH` by column position, so it
  works on Windows in any language.
- `PcChecker.exe --scheduled <job>` runs without a window. If PC Checker is
  already open in your Windows session, the job is handed to it over a local
  named pipe tied to your session and account; otherwise it runs on its own. Each run stops after 10
  minutes at most, writes its result to
  `%LOCALAPPDATA%\PcChecker\schedules\<job>-last.json` and the log, shows a
  notification and exits with code 0 (success) or 1 (failure), which Task
  Scheduler shows as the task's last result.
- *Notify only when something needs attention* (on by default) keeps quiet
  runs quiet. Turn it off to get a notification after every run. Clicking a
  notification opens the matching page (or the report).
- If you move `PcChecker.exe`, the tasks still point at the old path. PC
  Checker notices on start (a Dashboard notice) and Settings → Schedules shows
  *Fix tasks*, which points every enabled task at the current file. Self-updates
  keep the same path, so they need nothing.
- Creating or removing a task is recorded in the Action Journal. No restore
  point is made for it: a scheduled task is not a system-file change and is
  removed with one switch.
- *Reset to defaults* also removes PC Checker's tasks and the *Start with
  Windows* task.

Limitations: tasks run in your Windows session, so Task Scheduler skips a run
while you are signed out or the PC is off or asleep; the next scheduled time
runs as normal. In demo mode nothing is scheduled ("Demo mode: schedules are
not created") and the page shows sample results.

---

## Quick search and welcome

### Quick search

Press **Ctrl+K** anywhere in the window, or click *Search* at the top of the
navigation pane, to open a search box over the page.

- **What it finds**: every page and tab, every option on the Settings page
  (plus a few that live on their own pages: the overlay hotkey and opacity,
  the logging level, hiding serial numbers in logs and the cleanup dry run),
  and quick actions: *Run a full scan*, *Find temporary files to clean*
  (opens Cleanup and scans; nothing is removed until you confirm), *Check for
  driver updates*, *Optimize this PC*, *Export a full report*, *Show or hide
  the overlay*, *Run a latency check*, *Run a disk benchmark*, *Check health
  now*, *Run a speed check*, *Check for PC Checker updates*, *Show the
  welcome again*, *Back up your setup* and *Restore your setup from a file*.
- **Matching**: case, accents and punctuation do not matter. It matches the
  whole title, the start of it, every word you type at the start of (or inside)
  a word, the initials ("wf" for Windows Features, "km" for Keyboard & Mouse)
  and letters in order from the start of a word. A page is also found by its
  section ("monitor power") and by everyday words ("uninstall" finds Apps,
  "junk" finds Cleanup). In a translated interface both the translated and the
  English words work.
- **Recent first**: the items you opened last (up to eight, kept in settings)
  are listed first when the box is empty, followed by the quick actions, and
  are lifted above similar matches while you type.
- **Keys**: Up and Down move the highlight, Page Up and Page Down jump, Enter
  opens, Esc (or a click outside the box) closes. Ctrl+K again also closes it.
- **What opening does**: a page or tab opens; a setting opens its page, opens
  the collapsed section it is in, scrolls it into view and flashes it briefly;
  a quick action opens its page and presses the same button you would, so its
  confirmation dialog and safety steps still apply (a disk benchmark, for
  example, still asks first). Nothing changes the PC without asking.

### Welcome

The first time PC Checker opens, a short welcome explains what it does in three
lines, lets you choose the language (it applies after a restart, with a
*Restart now* button) and explains Advanced mode with a switch for it. Choose
*Run a full scan* to start straight away or *Look around first* to close it.
It appears once: never in screenshot runs or `--scheduled` runs, and not for
people upgrading from an earlier version (their settings file already exists).
**Settings → About → Show welcome again** (or *Show the welcome again* in quick
search) brings it back.

### Help, shortcuts and undo

Small things that make the app easier for someone who is not a PC expert.

- **What needs attention** (top of the Dashboard): up to five plain-language
  recommendations built from what PC Checker already read, most urgent first:
  Windows Update turned off or updates waiting, a restart to finish updates,
  a nearly full drive (the system drive at 95 % or under 5 GB free is urgent),
  devices with a driver problem, two or more heavy startup apps, PawnIO
  missing (so CPU and motherboard fans can't be read), no check-up yet or the
  last one 14 days ago or more, and what the last check-up left to do. Each has
  one button: *Review*, *Free up space*, *Install updates*, *Check drivers*,
  *Run check-up* or *Install* (PawnIO, after the usual confirmation). With
  nothing to do it says *Everything looks good* and when the PC was last
  checked. Live problems (temperatures, critical health items) stay in the
  Alerts card.
- **Page help**: every page header has a **?** button (or press **F1**) that
  says in two or three sentences what the page does and the safest first step,
  with a link to the matching section of these docs on GitHub.
- **Keyboard shortcuts**: press **?** anywhere outside a text box (or search
  for *Show keyboard shortcuts*) for a list of the keys that work everywhere
  (Ctrl+K, F1, F5, Ctrl+Tab, F6, Alt+letter, Shift+F10, Esc) and the extra keys
  of the page you are on (for example Delete and Enter on Processes).
- **F5** presses the page's own refresh or scan button on the Dashboard,
  Check-up, Health, Overview, Battery, Displays, Keyboard & Mouse, History,
  Power, Fans, Apps, Leftovers, Windows Update, Startup, Cleanup, Registry,
  Speed check and Logs. It does nothing while the page is busy, and anything
  that changes the PC still asks first.
- **Undo**: after a reversible change succeeds (turning a startup app on or
  off on the Startup or Performance page, a service's startup type there, a
  new power plan, one Optimize tweak that needs no restart), the green
  notification has an **Undo** button for 10 seconds. Undo asks first, then
  does the change the other way round through the same safety steps, and both
  are in the Action Journal. Only the notification about that change carries
  the button. Changes made together (Check-up's *Fix selected*,
  *Apply all safe*) are not offered; the Action Journal says how to undo them.
- **Simple and Advanced mode**: tabs for pages that mostly matter to
  experienced users (Services, Old drivers, Windows Features, Registry,
  Overclock, Latency) carry a small *Advanced* chip; every page stays open to
  everyone. In their first session, new users see a closable *You're in Simple
  mode* hint on the Dashboard and in Settings, with a button to turn on
  Advanced mode.
- **What needs attention** follows what you do: the startup list is read
  live, and the card is read again when you come back to the Dashboard, so an
  app you turned off or space you freed drops out straight away.
- **Tray menu**: *Run a check-up* opens PC Checker and starts one.

## Accessibility

Every page works from the keyboard and with a screen reader, in the light and
dark themes and in Windows high-contrast mode.

- **Keys**: Ctrl+K quick search, F6 and Shift+F6 to move between the menu, the
  section tabs and the page, Ctrl+Tab and Ctrl+Shift+Tab (or Ctrl+Page Down
  and Ctrl+Page Up) for the next or previous tab, Esc to close a dialog, quick
  search or the tray popup, and Alt plus a letter for each page's main action
  (Alt+S *Scan*, Alt+R *Refresh* and so on). Read-only tables are one Tab stop
  with arrow keys inside; the fan curve is edited with the arrow keys, Insert
  and Delete.
- **Screen readers**: buttons, images, lists and tables have names; tooltips
  are read as help; decorative icons are skipped.
- **Colours**: status colours and small text meet WCAG AA contrast (4.5:1) on
  both themes, with deeper shades in the light theme. In high-contrast mode the
  app uses the Windows contrast colours throughout.

The full list of shortcuts, the colour table and the known gaps are in
[ACCESSIBILITY.md](ACCESSIBILITY.md).

---

## Updates

PC Checker is one exe that updates itself from GitHub Releases. It checks when
it opens and every 6 hours while it runs (also from the tray), on the Stable
or Beta channel, and picks the build for the PC: `PcChecker.exe` on x64,
`PcChecker-arm64.exe` on Windows on Arm.

*When a new version is available* (Settings → Updates) is one of:

- **Ask me** (default): a dialog with *Update now*, *Later* and *Skip this
  version*. A skipped version is not offered again; Settings shows it with
  *Offer it again*.
- **Download it, then ask to restart**: the update is downloaded and checked
  straight away, then PC Checker asks before it restarts.
- **Just tell me**: only a notification.

Every download is checked against GitHub's SHA-256 digest for the file, or the
`SHA-256 (file name)` line in the release notes, and checked again right
before it is installed; a file that does not match is deleted and never run.
Installing renames the running exe to `PcChecker.exe.old`, puts the new one in
its place and restarts; the `.old` file is removed on the next start.

## Reports

*Export full report* on the Dashboard writes hardware, health, performance,
software, drivers and sensors as a standalone HTML page (dark theme, print
friendly), JSON or Markdown. Reports contain the same data the app shows,
including serial numbers unless *Hide serial numbers and MAC addresses in logs* is on.

## Demo mode and screenshots

`PcChecker.exe --demo` runs every page with realistic sample data for a
gaming desktop and never touches the real machine or your settings.
`PcChecker.exe --screenshots <dir>` captures every page, the overlay and the
tray popup to PNG; the
`Screenshots` workflow does this whenever app code changes on `main` (and on
demand). See [DEMO-MODE.md](DEMO-MODE.md).
