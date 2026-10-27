# In-game overlay

A small, always-on-top panel that shows live readings while you play or work.
It is click-through, so it never steals the mouse from the game.

## Turning it on

**Overlay** page → **Show overlay**, or press the hotkey (default `Ctrl+Shift+O`).
The hotkey works globally while PC Checker is running, even minimised to the
tray. The on/off state is remembered: an overlay that was showing when you
closed PC Checker comes back the next time it starts.

If the hotkey is already taken by another program, the page says so and the
overlay still works from the page buttons; pick a different combination
(Ctrl, Alt, Shift or Win plus a key, e.g. `Ctrl+Alt+F9`).

## Metrics

Tick the ones you want and use the arrows to reorder. Each can have its own
label.

| Id | Shows |
|---|---|
| `fps`, `frametime`, `fps.low1` | Frame rate of the game in front, average frame time, and the 1 % low (see [FPS counter](#fps-counter)) |
| `cpu.temp`, `cpu.load`, `cpu.clock`, `cpu.power` | CPU temperature, usage, fastest core clock, package power |
| `gpu.temp`, `gpu.hotspot`, `gpu.load`, `gpu.clock`, `gpu.memclock`, `gpu.vram`, `gpu.power`, `gpu.fan` | GPU temperature, hot spot, usage, core and memory clocks, VRAM used, power, fan |
| `ram.used`, `ram.percent` | Memory in use (read from Windows, so always available) |
| `disk.temp`, `disk.activity` | Hottest drive, disk activity |
| `net.down`, `net.up` | Network throughput across all active adapters |
| `battery.percent`, `battery.watts` | Battery charge and discharge rate |
| `system.power` | Total power draw: the PSU sensor when there is one, otherwise the same estimate the Power page uses |
| `time` | Clock |

A metric with no sensor on your hardware shows `--` instead of a number.
Temperatures turn yellow at the warning level and red at the critical level
configured on the Temperatures page.

## Position

- **Corner**: top-left, top-right, bottom-left or bottom-right of the monitor
  your mouse is on when the overlay is shown.
- **Custom**: turn on **Edit position on screen**, drag the dashed panel where
  you want it and turn edit mode off again. The exact X/Y is saved and can also
  be typed in.

## Appearance

- **Opacity**, **size** (scales the whole panel), **font** and **font size**.
- **Layout**: a vertical list or a single horizontal strip.
- **Colours**: background, labels and values as hex (`#RRGGBB` or
  `#AARRGGBB`). The background panel can be turned off for text only.
- **Sparklines**: a small graph of the last minute next to each value.
- **Update interval**: 1 second by default; higher saves a little CPU.

Every change applies to the live overlay immediately and is saved a moment
later. The page shows a live preview over a sample game scene.

## Presets

Built-in presets: **Minimal** (CPU temp, GPU temp, GPU load in one strip),
**Full** (everything except battery, FPS first) and **Temps only**. Save your own with
**Save as preset** ("Benchmarking", "Laptop", ...). A preset stores the metric
selection and order, corner, opacity, size and layout; colours, font and the
hotkey are left as they are.

## FPS counter

Tick **FPS**, **Frame time** or **1% low** under Metrics. The overlay then
shows, for example, `FPS 144`, `Frame time 6.9 ms` and `1% low 98`.

How it works: like Intel's PresentMon, PC Checker starts a real-time event
tracing session (`PcChecker-FrameRate`) on the Windows graphics present events
(`Microsoft-Windows-DXGI` for Direct3D 10/11/12, `Microsoft-Windows-D3D9`, and
the `Microsoft-Windows-DxgKrnl` kernel events as a fallback for OpenGL and
Vulkan games) and counts the frames presented by the game in front.

- **FPS** is the number of frames in the last second.
- **Frame time** is the average time per frame in that second, in milliseconds.
- **1% low** is the average frame rate of the slowest 1 % of frames over the
  last 30 seconds. It shows stutter that the average hides.
- The measured game is the last program that was in front (PC Checker itself,
  Explorer and the Start menu are ignored), so the numbers keep coming while
  you look at this page. The live preview names the game being measured.
- When the game presents nothing for 2 seconds (loading screen, minimised,
  paused) the values show `--`, and a pause longer than 2 seconds starts the
  1 % low history again.
- The tracing session only runs while an FPS metric is shown in the overlay or
  the preview, and stops when PC Checker closes. A session left behind by a
  crash is stopped the next time the counter starts.

Requirements and limits:

- Event tracing needs administrator rights. PC Checker always runs as
  administrator, so this is normally met.
- If Windows refuses the session (another tool uses too many tracing sessions,
  or a company policy blocks them) the Overlay page says why in the **FPS
  counter** card and the metrics show `--`. Nothing else is affected.
- Some anti-cheat systems hide a protected game's frames from other programs;
  the FPS metrics then show `--` for that game.

## Game profiles

The **Game profiles** card on the Overlay page switches settings automatically
while a game runs:

| Setting | Choices |
|---|---|
| Overlay | Keep the current layout, or load a preset (built-in or your own); show or hide the overlay |
| Fans | Leave the fans alone, or apply a fan profile from the Fans page |
| Power plan | Leave it, or switch to a Windows power plan such as *High performance* |
| Put everything back when the game closes | On by default |

Add a profile with **Add running game** (start the game first; programs with
an open window are listed busiest first, by GPU use where Windows reports it)
or **Browse for .exe**. A profile matches the game's program file name
(`eldenring.exe`), ignoring upper/lower case and the folder it is installed
in. If several enabled profiles match the same program, the first one in the
list wins. Each profile has an on/off switch, **Edit** and **Remove**.

How switching works:

1. The game has to stay in front for **2 seconds** before its profile is
   applied, so alt-tabbing past it does nothing.
2. Before the first switch PC Checker remembers the overlay layout and
   visibility, the active fan profile and the active power plan.
3. Switching to another window does **not** switch back. Only the game's
   process closing does, and then everything the profile changed is put back
   (unless *Put everything back* is off).
4. When a second game with a profile comes to the front while the first is
   still running, the first profile is undone and the second applied.
5. Turning **Switch automatically** off, or switching off or removing the
   active profile, undoes it at once. Closing PC Checker undoes it too
   (when *Put everything back* is on).

Options: **Switch automatically** (on by default) and **Only when the game
fills the screen**, which ignores the game while it runs in a smaller window
(full screen and borderless full-screen windows both count).

The card shows the active profile, e.g. *Active: Elden Ring profile (since
21:04)*, and what happened last. Every switch is written to the log and the
Action Journal (module *Gaming*). Power plan switches go through the same
change guard as every other change but do not create a restore point: a plan
switch is undone instantly and happens every time the game starts. If fan
control is not available on the PC, the fan part of a profile is skipped and
the editor says so.

## Limitations

- The overlay is a transparent desktop window. It shows over games that run in
  **borderless window** or **windowed** mode. Exclusive full-screen games draw
  directly to the screen and hide every desktop window, including this one;
  switch the game to borderless.
- Some anti-cheat systems block overlays. If a game refuses to start with the
  overlay on, turn it off for that game.
- Values come from the same sensor service as the Temperatures page, so what
  is not available there is not available here either (RAM, network and disk
  activity are read from Windows directly and always work).

## Settings

Overlay options are stored under `Overlay` and game profiles under `Gaming`
(`Profiles`, `AutoSwitch`, `OnlyWhenFullscreen`) in
`%LOCALAPPDATA%\PcChecker\settings.json`.
