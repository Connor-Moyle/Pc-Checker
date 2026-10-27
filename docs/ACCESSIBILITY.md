# Accessibility

PC Checker should work for people who use a keyboard instead of a mouse, a
screen reader such as Narrator, NVDA or JAWS, a light or dark theme, or Windows
high-contrast mode. This page says what works today, which keys do what, how
the colours were chosen, and what is still missing.

The target is WCAG 2.1 level AA for everything PC Checker draws itself. The
controls come from WPF and the WPF UI library, which provide most of the
keyboard and screen-reader behaviour; this page covers what PC Checker adds on
top.

## Keyboard

Everything you can do with the mouse on a page you can also reach with the
keyboard, except the few drawings listed under [Known gaps](#known-gaps).

### Anywhere in the window

| Keys | What they do |
|---|---|
| **Ctrl+K** | Opens quick search over the page (press it again to close). Find any page, tab, setting or quick action by typing. |
| **Tab** / **Shift+Tab** | Moves to the next or previous control. On every page the order is the header actions first (the *More* menu, then the main buttons), then the page content from top to bottom. |
| **F6** / **Shift+F6** | Jumps between the three areas of the window: the side menu, the section tabs (when the section has them) and the page. |
| **Ctrl+Tab** / **Ctrl+Shift+Tab** | Opens the next or previous tab of the current section (for example Temperatures → Power → Processes). **Ctrl+Page Down** and **Ctrl+Page Up** do the same. While focus is in a page's own tabs (Drivers, Windows Update, Disk space, Windows Features, Logs), these keys switch those tabs instead. |
| **Enter** or **Space** | Presses the focused button, ticks a check box, flips a switch, opens a drop-down. |
| **Esc** | Closes the open dialog (the same as its *Cancel* or *Close* button; a drop-down inside the dialog closes first), closes quick search, and hides the tray popup. |
| **Alt + underlined letter** | Runs the main action of the page (see below). Hold Alt to see the underline. |

### Lists, tabs and menus

- **Side menu**: Tab and Shift+Tab move through the sections, Enter or Space
  opens one (F6 brings you back to the menu from anywhere).
- **Section tabs**: Tab moves into the row of tabs; Left and Right move between
  them and open the page straight away.
- **Data tables** (sensors, SMART attributes, event logs, memory modules and
  the others): a read-only table is one Tab stop. Inside it, the arrow keys
  move between rows and cells, Home and End go to the first and last cell of a
  row, Ctrl+Home and Ctrl+End to the first and last row, Page Up and Page Down
  scroll. On Processes, **Delete** ends the selected program (it asks first).
  Tables with switches, buttons or drop-downs in their rows (Processes, the
  Startup apps, logon tasks and services, driver updates, Windows updates) let
  Tab reach those controls, so you can expand a program group, turn a startup
  item on or off, remove it or change a service's start mode from the keyboard.
- **Quick search**: Up and Down move the highlight, Page Up and Page Down jump,
  Enter opens, Esc closes.
- **Fan curve** (Cooling & RGB → Fans): Tab to the curve, then Left and Right
  pick a point, Up and Down change its fan speed by 1 % (Shift for 5 %, Page Up
  and Page Down for 10 %), Ctrl+Left and Ctrl+Right move it along the
  temperature axis (it stays between its neighbours), Insert adds a point after
  it and Delete removes it. The selected point has a focus ring and its
  position is read out by screen readers.

### Main actions (access keys)

The main button of each page has an access key. Press Alt and the letter:

| Page | Keys |
|---|---|
| Dashboard | Alt+F *Full scan* |
| Cleanup, Disk space, Old drivers, Hardware → Overview, Registry, Apps | Alt+S *Scan* |
| Drivers | Alt+S *Scan drivers* |
| Speed check | Alt+S *Scan for slowdowns* |
| Health | Alt+A *Analyze* |
| Benchmark | Alt+R *Run benchmark* |
| Latency | Alt+R *Run latency check* |
| Network | Alt+R *Run all tests* |
| Audio, Displays, Keyboard & Mouse, Startup, Storage, Windows Features | Alt+R *Refresh* |
| Fans | Alt+D *Detect fans* |
| History | Alt+E *Export CSV* |
| Logs | Alt+E *Export diagnostics* |
| Windows Update | Alt+C *Check for updates* |

In a translated interface the key moves to the same letter in the translated
label when it has one, otherwise to its first letter.

### Focus you can see

Every control shows a focus ring when you reach it with the keyboard (WPF UI's
Fluent focus visual). The section tabs draw the same ring around the tab label,
and the fan curve rings the selected point.

## Screen readers

- **Every control has a name.** Buttons that show only an icon (delete, copy,
  move up, dismiss and so on) have a written name, and so do images, lists and
  data tables. Where the XAML leaves a name out, PC Checker takes it from the
  text a sighted user reads as the label: the setting card a switch sits in,
  the heading above a list, the words beside a box or the text inside a button.
  These names follow the interface language.
- **Tooltips are read as help.** A control whose tooltip explains it (most
  buttons in the page headers do) exposes that text as its help text, so a
  screen reader announces it after the name.
- **Icons are quiet unless they mean something.** Decorative icons (the icon in
  a stat tile, an empty-state picture, the bullet icons in the welcome) are
  hidden from screen readers. An icon that carries meaning on its own, such as
  the *device not connected* marker on Old drivers or *Main display* on
  Displays, is announced as an image with its name.
- **The title bar buttons** (Minimize, Maximize, Close) have their names.
- **Section tabs** are announced by page name, as a list you move through with
  the arrow keys.

## Colour and contrast

The interface uses the Fluent colours of the WPF UI theme plus a small palette
of status colours of its own (green for good, yellow and orange for warnings,
red for problems, and cyan, purple, blue and grey for categories). Every status
is also written as text: colour is never the only way something is shown.

The palette is checked against WCAG AA by the test suite
(`tests/PcChecker.Core.Tests/AccessibilityTests.cs`): every colour must reach
4.5:1 as text on the window background, a card and the layer used for hovered
rows, in its theme, and the text on a coloured badge must reach 4.5:1 on every
colour. The light theme has its own, deeper shades, because the bright colours
of the dark theme fade into a white page (they reach only 1.4:1 to 2.5:1 there).

| Colour | Dark theme | Lowest ratio | Badge text (#111111) | Light theme | Lowest ratio | Badge text (white) |
|---|---|---|---|---|---|---|
| Cyan | `#4CC2FF` | 6.9:1 | 9.4:1 | `#0369A1` | 5.3:1 | 5.9:1 |
| Green | `#22C55E` | 6.0:1 | 8.3:1 | `#147A3A` | 4.9:1 | 5.4:1 |
| Yellow | `#FACC15` | 9.0:1 | 12.3:1 | `#965A00` | 5.0:1 | 5.6:1 |
| Orange | `#F97316` | 4.9:1 | 6.7:1 | `#C2410C` | 4.7:1 | 5.2:1 |
| Red | `#F87171` | 5.0:1 | 6.8:1 | `#B91C1C` | 5.8:1 | 6.5:1 |
| Purple | `#A78BFA` | 5.1:1 | 6.9:1 | `#6D28D9` | 6.4:1 | 7.1:1 |
| Grey | `#9CA3AF` | 5.4:1 | 7.4:1 | `#4B5563` | 6.8:1 | 7.6:1 |
| Blue | `#60A5FA` | 5.4:1 | 7.4:1 | `#1D4ED8` | 6.0:1 | 6.7:1 |

What changed in this pass:

- **Red** in the dark theme is a little lighter (`#EF4444` reached only 3.8:1
  on a card).
- **Badge text** follows the theme: near-black on the bright dark-theme
  colours, white on the deeper light-theme ones. Some badges used white text on
  bright green, cyan or yellow (2:1 or less).
- **Small notes** (the *Caption* style, stat tile subtitles and chart axis
  labels) used the theme's "tertiary" grey, which WPF UI marks as not
  accessible and which reaches only 3.3:1 on a light page. They now use a
  slightly darker grey there (4.8:1); the dark theme is unchanged (5.0:1).
- **Charts** (History, sparklines, the fan curve, the disk block map) draw their
  lines in the palette for the current theme, so a line is at least 3:1 against
  the plot in both themes.
- **The display arrangement** on Displays now writes the display numbers in the
  theme's text colour instead of white, which vanished in the light theme.

The colours live in `src/PcChecker.Core/Accessibility/AccentPalette.cs`;
`src/PcChecker.App/Services/ThemeColors.cs` puts the right set in place when
the theme changes.

## High contrast

When Windows high-contrast mode is on (Settings → Accessibility → Contrast
themes), PC Checker switches WPF UI to its high-contrast theme, whatever the
*Theme* setting says, and replaces its own colours with the system ones: status
colours and accents use the contrast theme's highlight colour, text on badges
uses its highlight text colour, and small notes use the normal text colour. So
text stays readable in Aquatic, Desert, Dusk, Night sky or a custom contrast
theme. Turning high contrast on or off, or switching from one contrast theme to
another, while PC Checker runs takes effect at once; turning it off returns to
the theme you chose.

In high contrast the status colours all look the same; the words on the badges
(*Good*, *Warning*, *Safe*, *Restart needed* and so on) still say which is which.

## For contributors

The rules are checked by `XamlAccessibilityTests` and `AccessibilityTests`:

- A button with only an icon needs `AutomationProperties.Name` (a short verb
  phrase such as "Delete profile"); keep the longer explanation in `ToolTip`.
  An `Image` needs a name too, and so does every list and data grid.
- Standalone icons use `controls:IconGlyph`, not `ui:SymbolIcon`. Screen readers
  skip it unless it has a name or a text tooltip. The `{ui:SymbolIcon …}` markup
  extension on a button's `Icon` is fine.
- Use the theme brushes through `DynamicResource`: `AccentGreenBrush` and the
  other accents, `OnAccentTextBrush` for text on a solid accent colour,
  `OnAccentIconBrush` for an icon on a coloured tile and `CaptionTextBrush` for
  small notes. Do not hard-code white or black text: it disappears on one of
  the themes. Status colours in code come from `ThemeColors.Accent(...)` or the
  status converters.
- Never set `FocusVisualStyle` to `{x:Null}`; give custom templates a visible
  focus state instead.
- Access keys go on the page's main action only, through an `AccessText`
  (`<AccessText Text="_Scan" />`), because WPF UI's button does not turn an
  underscore in plain content into a key. Keep them unique on a page.
- A read-only `ui:DataGrid` gets `KeyboardNavigation.TabNavigation="Once"` so it
  is one Tab stop with arrow keys inside. Never use it on a grid whose rows hold
  buttons, switches or drop-downs: arrow keys move between cells but do not
  reach the controls inside them.
- `ThemeColors.Accent(...)` hands out one shared brush per colour that changes
  colour with the theme. Bind it as it is; do not freeze it or copy its colour,
  or the badge keeps the old shade after a theme change.

## Known gaps

- **Charts are pictures.** The history charts, sparklines, gauges and the disk
  block map are drawings; a screen reader does not walk their points. The
  numbers they show are on the page as text (current, average and peak values,
  tile values and the block counts). The History page's *Export CSV* gives the
  full data.
- **Hidden icons are only hidden from the normal views.** WPF has no way to
  remove an element from the raw UI Automation tree, so inspection tools still
  see decorative icons there; Narrator, NVDA and JAWS use the normal views and
  skip them.
- **Automatic names are a best guess.** A control without a name in the XAML
  gets one from the text next to it. Most read well; if one does not, please
  report it (and the fix is a name in the XAML).
- **Mouse-first editors.** The colour field of the lighting colour picker and
  the display arrangement map are mouse-only; the colour picker has a hex box
  and preset swatches you can reach with the keyboard, and the arrangement is
  edited in Windows display settings (*Rearrange* opens it).
- **The in-game overlay** draws over games with the colours you pick on the
  Overlay page, ignores the mouse and keyboard so it never steals input from a
  game, and is not exposed to screen readers.
- **WPF UI's own parts** (the spin buttons of number boxes, the clear button of
  text boxes) are named by the library; some of them have no name yet.
- **Not every screen reader has been tried on every page.** The checks above
  are automated and the behaviour comes from WPF's UI Automation support;
  reports from people who use NVDA, JAWS or Narrator every day are very welcome.
