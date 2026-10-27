# UI guidelines

PC Checker does a lot, so every page has to stay calm. These rules keep the
pages consistent. Follow them when you add or change a page.

## Navigation

- The side menu has one item per **section**. A section with several pages
  shows them as **tabs** above the content.
- `src/PcChecker.App/Services/NavigationCatalog.cs` is the only place pages are
  listed. The menu, the tabs and the screenshot runner all read it.
- A tab label is short (one or two words). The page's own header title matches
  the tab label.
- Do not add a top-level section for a single new page if an existing section
  fits. Aim for no more than about a dozen menu items in total.

## Page anatomy

Every page follows the same order, top to bottom:

1. **Header** (`controls:SectionHeader`): title, a one-line subtitle, and the
   actions.
2. **Status**: errors and banners (`ui:InfoBar`), only while they apply.
3. **Summary**: the few numbers that answer "how is it?", as stat tiles.
4. **Main content**: the list, the controls or the results.
5. **Options and advanced**: collapsed `ui:CardExpander` sections at the end.

## Header

- Subtitle: one sentence, under about 90 characters, or a live status line
  such as "Checked 2 minutes ago".
- At most **one primary button** and **one secondary button**. Button text
  is a verb: "Scan now", "Run check-up", "Clean selected", "Detect fans".
  A rare, hard-to-undo action (reset everything) goes in **More**, not in the
  header as a red button.
- The **?** help button next to the title comes with `controls:SectionHeader`;
  you do not add it. Its text lives in `src/PcChecker.Core/Shell/HelpCatalog.cs`,
  keyed by the page's file name in `NavigationCatalog`: what the page does
  (one sentence), the safest first step (one or two sentences) and the docs
  heading it links to. A new page needs an entry there; a test fails without
  one, and another checks that the docs heading exists. F1 opens the same
  help.
- Every other action goes in a **More** drop-down:

  ```xml
  <ui:DropDownButton Content="More" Icon="{ui:SymbolIcon MoreHorizontal24}" Margin="0,0,8,0">
    <ui:DropDownButton.Flyout>
      <ContextMenu DataContext="{Binding PlacementTarget.DataContext, RelativeSource={RelativeSource Self}}">
        <MenuItem Header="Export…" Command="{Binding ExportCommand}" />
      </ContextMenu>
    </ui:DropDownButton.Flyout>
  </ui:DropDownButton>
  ```

- Settings that change how a scan runs (durations, toggles) do not live in the
  header. Put them in an **Options** expander.

## Tiles and grids

- Stat tiles and repeated cards go in `controls:AdaptiveGrid`, never a
  `WrapPanel`. It fits as many equal columns as the width allows, so rows line
  up. Typical values: `MinItemWidth="200"` for stat tiles and `"320"` or more
  for cards.
- Items inside an `AdaptiveGrid` have no outer margin; the grid's `Spacing`
  handles the gaps.

## Text

- One short line of help under a heading at most. Longer explanations go in a
  `ToolTip`, a collapsed "About this" expander, or the docs.
- Say the same thing once. Do not repeat a value in a banner, a tile and a
  table.
- Information that is only useful once (what can be controlled, how a score
  works) goes in a closable `ui:InfoBar` or a collapsed expander.

## Spacing

- Cards: `Padding="16"`, `Margin="0,0,0,12"` between stacked cards. Side by
  side cards use a `Grid` with a 12 px gap.
- Card titles use the `CardTitle` style. Secondary text uses `Muted`; small
  notes use `Caption`.
- Page margins come from the `PageRoot` style; do not add extra outer margins.

## Advanced mode

- Advanced-only content is hidden, not disabled, when Advanced mode is off.
- Risky actions sit in the advanced area, below the everyday controls.
- A page whose main job is a deep or risky change gets an *Advanced* chip on
  its tab: add its key to `SimpleModeRules.AdvancedPageKeys`
  (`src/PcChecker.Core/Shell/SimpleModeRules.cs`). The page itself stays open
  to everyone.
- New users see a one-time *You're in Simple mode* hint on the Dashboard and in
  Settings (`SimpleModeHintViewModel`); do not add more hints like it.

## Keyboard

- **F1** opens the page's help, **?** (typed outside a text box) lists every
  shortcut, **F5** presses the page's refresh or scan button.
- To support F5, let the page's view model implement `IRefreshablePage`
  (`src/PcChecker.App/ViewModels/RefreshablePages.cs` has the existing ones)
  and return the header's own command, so its busy check and confirmations
  still apply. Leave out heavy or risky "refreshes" (reloading drivers).
- Page keys (Delete, Enter, Ctrl+F…) are listed in
  `KeyboardShortcutCatalog` so the shortcuts list shows them; keep it in step
  with the page.
- Rows with actions get a right-click menu with the same commands
  (Shift+F10 opens it from the keyboard).

## Notifications and Undo

- After a change succeeds, show `INotificationService.Success(...)` with what
  happened in plain words.
- When the same service can take the change straight back, run it through
  `UndoOffers.RunAsync(change, undo, description)` (or set
  `ChangeOptions.Undo` in the service). The success notification then offers
  **Undo** for 10 seconds. The undo must call the service again (so it is
  guarded and journaled) and put the page's row back.
- Do not offer Undo for batches, deletions, uninstalls or anything that needs a
  restart; the Action Journal says how to undo those.

## Dashboard: What needs attention

- The card at the top of the Dashboard lists at most five recommendations from
  `AttentionRules` (`src/PcChecker.Core/Shell/AttentionRules.cs`), most urgent
  first, each with exactly one button that opens a page or starts an action
  that confirms first.
- A rule only reads results the app already has; an unknown value never makes
  an item. Write the title as the problem ("Drive C: is 92 % full"), the
  detail as why it matters, and the button as a verb ("Free up space").
- Live problems (temperatures, critical health items) stay in the Alerts card,
  so nothing is said twice.

## Empty and busy states

- Before the first scan a page shows one clear call to action
  (`controls:EmptyState`), not empty tables.
- Every empty state says what to do next and, where there is one, has the
  button for it in `EmptyState.Action` ("Reload sensors", "Detect fans").
- Long operations use `controls:BusyOverlay` with a cancel button where
  possible.

## Accessibility

- Icon-only buttons, images, lists and data grids get
  `AutomationProperties.Name`; the tooltip keeps the longer explanation.
- Standalone icons are `controls:IconGlyph`, not `ui:SymbolIcon`.
- Colours come from the theme through `DynamicResource`: the accent brushes,
  `OnAccentTextBrush` for badge text, `CaptionTextBrush` for small notes. No
  hard-coded white or black text.
- Keep keyboard focus visible (no `FocusVisualStyle="{x:Null}"`), give the
  page's main action an access key with `<AccessText Text="_Scan" />`, and make
  read-only data grids one Tab stop (`KeyboardNavigation.TabNavigation="Once"`).

Details and the reasons are in [ACCESSIBILITY.md](ACCESSIBILITY.md).

## Checks

The test project checks some of these rules automatically
(`XamlConventionsTests` and `XamlAccessibilityTests`), for example that
templated WPF UI styles are based on the library defaults, that broken icon
names are not used, and the accessibility rules above.
