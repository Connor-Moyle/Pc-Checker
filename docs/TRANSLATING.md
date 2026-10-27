# Translating PC Checker

PC Checker ships in English, German, Spanish, French, Italian and Brazilian
Portuguese. Pick the language in **Settings → Appearance → Language**, or
leave it on *Windows language* to follow the Windows display language. The
change applies after a restart.

## What is translated

- Everything written into the pages: titles, tabs, buttons, labels, tooltips,
  hints, menus and empty-state messages.
- Section and tab names, confirmation dialogs and notifications.

Results that the app builds while it runs (scan findings, measured values,
log lines and the Ask Claude conversation) are still in English. Text with no
translation is always shown in English, never as a blank or a key.

## How it works

- `src/PcChecker.App/Localization/strings.txt` lists every English string
  the interface uses, one per line. `build/extract-strings.py` writes it from
  the XAML pages, the navigation catalog, any C# text wrapped in
  `Loc.T("…")` or `Loc.F("…", …)`, and the quick search catalogs in
  `src/PcChecker.Core/Shell`, where text is marked with `T("…")`. CI fails when the list is out of date, so
  run the script after changing interface text:

  ```
  python3 build/extract-strings.py
  ```

- `src/PcChecker.App/Localization/Languages/<code>.json` maps each English
  string to its translation. The files are embedded in the exe.
- At startup the app loads the chosen language and translates each element's
  fixed text as it appears on screen. Bound values from view models are left
  alone.

## Improving a translation

1. Open `Languages/<code>.json` and edit the value next to the English key.
2. Keep placeholders such as `{0}` exactly as they are (the tests check
   this), keep product names and technical abbreviations, and keep labels as
   short as the English.
3. Run the tests (`dotnet test`) and open a pull request.

## Adding a language

1. Add the language to `LanguageCatalog` in
   `src/PcChecker.Core/Localization/LanguageCatalog.cs`.
2. Create `Languages/<code>.json` with a translation for every line of
   `strings.txt`. The tests require at least 90 % coverage.
3. Build, run the tests and open a pull request.

Screenshot runs (`--screenshots`) always use English so the README images
match.
