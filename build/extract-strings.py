#!/usr/bin/env python3
"""Collects the English interface text that PC Checker translates and writes it, one string per line, to
src/PcChecker.App/Localization/strings.txt. Translators fill in src/PcChecker.App/Localization/Languages/<code>.json
(English text -> translated text); anything missing stays English.

Sources: literal XAML attributes that hold interface text, setters of those properties, section and page names in
NavigationCatalog.cs, and C# string literals passed to Loc.T(...) or Loc.F(...).

Usage: python3 build/extract-strings.py [--check]   (--check fails when strings.txt is out of date)
"""
import html, pathlib, re, sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
APP = ROOT / "src" / "PcChecker.App"
OUT = APP / "Localization" / "strings.txt"
TEXT_ATTRS = ("Text", "ToolTip", "Content", "Title", "Header", "Message", "Subtitle", "PlaceholderText",
              "Description", "Label", "Unit", "Caption", "Reason", "ActionText")
ATTR = re.compile(r'\b(?:[a-zA-Z]+:)?(?:[A-Z][A-Za-z]*\.)?(' + "|".join(TEXT_ATTRS) + r')="([^"]*)"')
SETTER = re.compile(r'<Setter\s+Property="(?:[A-Za-z]+\.)?(' + "|".join(TEXT_ATTRS) + r')"\s+Value="([^"]*)"')
CODE = re.compile(r'\bLoc\.[TF]\(\s*"((?:[^"\\]|\\.)*)"')
CATALOG = re.compile(r'\b(?:Section|Footer|Page)\("([^"]+)"')


def wanted(text: str) -> bool:
    text = text.strip()
    if not text or text.startswith("{") or not re.search(r"[A-Za-z]{2}", text):
        return False
    if re.fullmatch(r"[A-Za-z]+\d+|#[0-9A-Fa-f]+|[\w.-]+\.(png|ico|svg|json|xaml)", text):
        return False
    return True


def collect():
    found = set()
    for path in APP.rglob("*.xaml"):
        if "obj" in path.parts or "bin" in path.parts:
            continue
        source = path.read_text(encoding="utf-8")
        for match in list(ATTR.finditer(source)) + list(SETTER.finditer(source)):
            value = html.unescape(match.group(2))
            if wanted(value):
                found.add(value)
    for path in APP.rglob("*.cs"):
        if "obj" in path.parts or "bin" in path.parts:
            continue
        source = path.read_text(encoding="utf-8")
        for match in CODE.finditer(source):
            found.add(bytes(match.group(1), "utf-8").decode("unicode_escape"))
        if path.name == "NavigationCatalog.cs":
            found.update(m.group(1) for m in CATALOG.finditer(source))
    for extra in ("Cancel", "OK", "Continue"):
        found.add(extra)
    return sorted(found, key=str.casefold)


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
