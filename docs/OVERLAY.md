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
**Full** (everything except battery) and **Temps only**. Save your own with
**Save as preset** ("Benchmarking", "Laptop", ...). A preset stores the metric
selection and order, corner, opacity, size and layout; colours, font and the
hotkey are left as they are.

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

Everything on this page is stored under `Overlay` in
`%LOCALAPPDATA%\PcChecker\settings.json`.
