#!/usr/bin/env python3
"""Collects the English interface text that PC Checker translates and writes it, one string per line, to
src/PcChecker.App/Localization/strings.txt. Translators fill in src/PcChecker.App/Localization/Languages/<code>.json
(English text -> translated text); anything missing stays English.

Sources: literal XAML attributes that hold interface text (access key markers removed), setters of those properties,
screen-reader names and help text (AutomationProperties.Name and HelpText), section and page names in
NavigationCatalog.cs, C# string literals passed to Loc.T(...) or Loc.F(...), and the quick search catalogs in
src/PcChecker.Core/Shell, where interface text is marked with T("...").

Usage: python3 build/extract-strings.py [--check]   (--check fails when strings.txt is out of date)
"""
import html, pathlib, re, sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
APP = ROOT / "src" / "PcChecker.App"
OUT = APP / "Localization" / "strings.txt"
CORE_SHELL = ROOT / "src" / "PcChecker.Core" / "Shell"
TEXT_ATTRS = ("Text", "ToolTip", "Content", "Title", "Header", "Message", "Subtitle", "PlaceholderText",
              "Description", "Label", "Unit", "Caption", "Reason", "ActionText")
ATTR = re.compile(r'\b(?:[a-zA-Z]+:)?(?:[A-Z][A-Za-z]*\.)?(' + "|".join(TEXT_ATTRS) + r')="([^"]*)"')
SETTER = re.compile(r'<Setter\s+Property="(?:[A-Za-z]+\.)?(' + "|".join(TEXT_ATTRS) + r')"\s+Value="([^"]*)"')
# Screen-reader names and help text written in the XAML.
AUTOMATION = re.compile(r'\bAutomationProperties\.(Name|HelpText)="([^"]*)"')
# An access key marker at the start of a word ("_Scan", "Check for _updates"); translations are keyed without it.
ACCESS_KEY = re.compile(r'(?:(?<=^)|(?<=\s))_(?=[A-Za-z0-9])')
CODE = re.compile(r'\bLoc\.[TF]\(\s*"((?:[^"\\]|\\.)*)"')
DESCRIPTION = re.compile(r'\bDescription\(\s*"((?:[^"\\]|\\.)*)"\s*\)')
CATALOG = re.compile(r'\b(?:Section|Footer|Page)\("([^"]+)"')
MARKED = re.compile(r'(?<![\w.])T\(\s*"((?:[^"\\]|\\.)*)"')


def wanted(text: str) -> bool:
    text = text.strip()
    if not text or text.startswith("{") or not re.search(r"[A-Za-z]{2}", text):
        return False
    if re.fullmatch(r"[A-Za-z]+\d+|#[0-9A-Fa-f]+|[\w.-]+\.(png|ico|svg|json|xaml)", text):
        return False
    return True


def unescape_csharp(text: str) -> str:
    """Undoes C# string escapes (\\n, \\", \\\\, \\u2026, \\x41) without touching other characters such as an ellipsis."""
    simple = {"n": "\n", "t": "\t", "r": "\r", "0": "\0", "a": "\a", "b": "\b", "f": "\f", "v": "\v"}

    def replace(m: re.Match) -> str:
        if m.group(1):
            return chr(int(m.group(1), 16))
        if m.group(2):
            return chr(int(m.group(2), 16))
        if m.group(3):
            return chr(int(m.group(3), 16))
        ch = m.group(4)
        return simple.get(ch, ch)

    return re.sub(r"\\(?:u([0-9A-Fa-f]{4})|U([0-9A-Fa-f]{8})|x([0-9A-Fa-f]{1,4})|(.))", replace, text)


def collect():
    found = set()
    for path in APP.rglob("*.xaml"):
        if "obj" in path.parts or "bin" in path.parts:
            continue
        source = path.read_text(encoding="utf-8")
        for match in list(ATTR.finditer(source)) + list(SETTER.finditer(source)) + list(AUTOMATION.finditer(source)):
            value = ACCESS_KEY.sub("", html.unescape(match.group(2)), count=1)
            if wanted(value):
                found.add(value)
    for path in APP.rglob("*.cs"):
        if "obj" in path.parts or "bin" in path.parts:
            continue
        source = path.read_text(encoding="utf-8")
        for match in CODE.finditer(source):
            found.add(unescape_csharp(match.group(1)))
        for match in DESCRIPTION.finditer(source):
            found.add(unescape_csharp(match.group(1)))
        if path.name == "NavigationCatalog.cs":
            found.update(m.group(1) for m in CATALOG.finditer(source))
    for path in (ROOT / "src" / "PcChecker.Core").rglob("*.cs"):
        if "obj" in path.parts or "bin" in path.parts:
            continue
        for match in DESCRIPTION.finditer(path.read_text(encoding="utf-8")):
            found.add(unescape_csharp(match.group(1)))
    for path in CORE_SHELL.glob("*.cs"):
        for match in MARKED.finditer(path.read_text(encoding="utf-8")):
            found.add(unescape_csharp(match.group(1)))
    for extra in ("Cancel", "OK", "Continue"):
        found.add(extra)
    return sorted(found, key=lambda s: (s.casefold(), s))


def main() -> int:
    strings = collect()
    text = "\n".join(s.replace("\n", "\\n") for s in strings) + "\n"
    if "--check" in sys.argv:
        current = OUT.read_text(encoding="utf-8") if OUT.exists() else ""
        if current != text:
            print("strings.txt is out of date; run python3 build/extract-strings.py", file=sys.stderr)
            return 1
        return 0
    OUT.write_text(text, encoding="utf-8")
    print(f"{len(strings)} strings -> {OUT.relative_to(ROOT)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
