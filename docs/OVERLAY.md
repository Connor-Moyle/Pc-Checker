# In-game overlay

A small, always-on-top panel that shows live readings while you play or work.
It is click-through, so it never steals the mouse from the game.

## Turning it on

**Overlay** page → **Enable**, or press the hotkey (default `Ctrl+Shift+O`).
The hotkey works globally while PC Checker is running, even minimised to the
tray.

## Metrics

Tick the ones you want and drag to reorder. Each can have its own label.

| Id | Shows |
|---|---|
| `cpu.temp`, `cpu.load`, `cpu.clock`, `cpu.power` | CPU temperature, usage, clock, package power |
| `gpu.temp`, `gpu.hotspot`, `gpu.load`, `gpu.clock`, `gpu.memclock`, `gpu.vram`, `gpu.power`, `gpu.fan` | GPU temperature, hotspot, usage, core and memory clocks, VRAM used, power, fan |
| `ram.used`, `ram.percent` | Memory in use |
| `disk.temp`, `disk.activity` | Hottest drive, disk activity |
| `net.down`, `net.up` | Network throughput |
| `battery.percent`, `battery.watts` | Battery charge and discharge rate |
| `system.power` | Estimated total power draw |
| `time` | Clock |

A metric with no sensor on your hardware shows a dash instead of a number.

## Appearance

- **Position**: a corner, or *Custom* with exact X/Y.
- **Opacity**, **scale**, **font** and **size**.
- **Orientation**: a vertical list or a single horizontal row.
- **Colours**: background, text and accent. Background can be turned off for
  text-only.
- **Sparklines**: a small history graph next to each value.
- **Update interval**: 1 second by default; higher saves a little CPU.

## Presets

Save the current layout as a preset ("Benchmarking", "Minimal", "Laptop") and
switch between them from the page or the tray menu.

## Limitations

- The overlay is a transparent desktop window. It shows over games that run in
  **borderless window** or **windowed** mode. Exclusive full-screen games draw
  directly to the screen and hide every desktop window, including this one;
  switch the game to borderless.
- Some anti-cheat systems block overlays. If a game refuses to start with the
  overlay on, turn it off for that game.
- Values come from the same sensor service as the Temperatures page, so what
  is not available there is not available here either.

## Settings

Everything on this page is stored under `Overlay` in
`%LOCALAPPDATA%\PcChecker\settings.json`.
