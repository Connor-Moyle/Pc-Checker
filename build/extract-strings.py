#!/usr/bin/env python3
"""Collects the English interface text that PC Checker translates and writes it, one string per line, to
src/PcChecker.App/Localization/strings.txt. Translators fill in src/PcChecker.App/Localization/Languages/<code>.json
(English text -> translated text); anything missing stays English.

Sources: literal XAML attributes that hold interface text (access key markers removed), setters of those properties,
screen-reader names and help text (AutomationProperties.Name and HelpText), section and page names in
NavigationCatalog.cs, C# string literals passed to Loc.T(...) or Loc.F(...), plain string literal arguments of the
dialog and notification calls that translate at run time (IDialogService.ConfirmAsync/ShowMessageAsync/ChooseAsync,
INotificationService.Show/Info/Success/Warning/Error, TrayIconService.Notify), and the quick search catalogs in
src/PcChecker.Core/Shell, src/PcChecker.Core/AppHistory and the Crashes catalogs and rules in src/PcChecker.Core/Crashes, where
interface text is marked with T("...") or passed to a Translator as tr.T("...")/tr.F("...").

Usage: python3 build/extract-strings.py [--check]   (--check fails when strings.txt is out of date)
"""
import html, pathlib, re, sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
APP = ROOT / "src" / "PcChecker.App"
OUT = APP / "Localization" / "strings.txt"
CORE_SHELL = ROOT / "src" / "PcChecker.Core" / "Shell"
CORE_MARKED = (CORE_SHELL, ROOT / "src" / "PcChecker.Core" / "Crashes", ROOT / "src" / "PcChecker.Core" / "AppHistory")
# The Game ready rules write their text through tr.T("...")/tr.F("...") and mark catalog names with T("...").
CORE_MARKED += (ROOT / "src" / "PcChecker.Core" / "GameReady",)
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
# Core code that is handed a Translator (named tr): tr.T("...") and tr.F("...").
TRANSLATOR_CALL = re.compile(r'\btr\.[TF]\(\s*"((?:[^"\\]|\\.)*)"')
# Dialog and notification calls whose text arguments are translated when shown. The receiver is checked so that
# logger calls (_logger.Warning) and message boxes are left out.
RUNTIME_CALL = re.compile(r'\b(ConfirmAsync|ShowMessageAsync|ChooseAsync|Show|Info|Success|Warning|Error|Notify)\(')
RUNTIME_RECEIVER = re.compile(r'(?i)notification|dialog|tray')
PLAIN_LITERAL = re.compile(r'"((?:[^"\\]|\\.)*)"')
# A choice between two literals: enabled ? "Schedule created" : "Schedule removed".
TERNARY_LITERALS = re.compile(r'[^"?]*\?\s*"((?:[^"\\]|\\.)*)"\s*:\s*"((?:[^"\\]|\\.)*)"')


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


def skip_string(source: str, i: int) -> int:
    """Index just after the C# string literal starting at source[i] ("...", @"...", $"...", $@"..." or @$"...")."""
    prefix = ""
    while source[i] in "$@":
        prefix += source[i]
        i += 1
    verbatim, interpolated = "@" in prefix, "$" in prefix
    i += 1
    while i < len(source):
        ch = source[i]
        if ch == "\\" and not verbatim:
            i += 2
        elif ch == '"' and verbatim and source[i + 1:i + 2] == '"':
            i += 2
        elif ch == '"':
            return i + 1
        elif ch == "{" and interpolated and source[i + 1:i + 2] == "{":
            i += 2
        elif ch == "{" and interpolated:
            i = skip_code(source, i + 1)
        else:
            i += 1
    return i


def skip_code(source: str, i: int) -> int:
    """Index just after the bracket that closes code starting at source[i], skipping strings and nested brackets."""
    depth = 0
    while i < len(source):
        ch = source[i]
        if ch == '"' or (ch in "$@" and source[i + 1:i + 3].lstrip("$@").startswith('"')):
            i = skip_string(source, i)
            continue
        if ch == "'":
            end = source.find("'", i + 2 if source[i + 1] == "\\" else i + 1)
            i = (end if end > 0 else i) + 1
            continue
        if ch in "([{":
            depth += 1
        elif ch in ")]}":
            if depth == 0:
                return i + 1
            depth -= 1
        i += 1
    return i


def call_arguments(source: str, start: int):
    """The top-level arguments of the call whose opening parenthesis is at source[start - 1], as source slices."""
    args, depth, i, begin = [], 0, start, start
    while i < len(source):
        ch = source[i]
        if ch == '"' or (ch in "$@" and source[i + 1:i + 3].lstrip("$@").startswith('"')):
            i = skip_string(source, i)
            continue
        if ch == "'":
            end = source.find("'", i + 2 if source[i + 1] == "\\" else i + 1)
            i = (end if end > 0 else i) + 1
            continue
        if ch in "([{":
            depth += 1
        elif ch in ")]}":
            if depth == 0:
                args.append(source[begin:i])
                return args
            depth -= 1
        elif ch == "," and depth == 0:
            args.append(source[begin:i])
            begin = i + 1
        elif ch == ";":
            return []
        i += 1
    return []


def runtime_texts(source: str):
    """Plain literal arguments (not interpolated, not concatenated, or a choice of two) of dialog and notification calls."""
    for match in RUNTIME_CALL.finditer(source):
        # The receiver expression: back to the previous space or statement character ("_dialogs.",
        # "services.GetService<INotificationService>()?."). Notify(...) is the tray's and the pages' own helper.
        receiver = re.split(r"[\s;{}=,!]", source[max(0, match.start() - 200):match.start()])[-1]
        if match.group(1) == "Notify" and receiver in ("", "this.", "_tray.", "tray.", "tray?."):
            pass
        elif not receiver.endswith(".") or not RUNTIME_RECEIVER.search(receiver):
            continue
        for arg in call_arguments(source, match.end()):
            literal = PLAIN_LITERAL.fullmatch(arg.strip()) or TERNARY_LITERALS.fullmatch(arg.strip())
            for text in literal.groups() if literal else ():
                if wanted(text):
                    yield unescape_csharp(text)


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
        found.update(runtime_texts(source))
        if path.name == "NavigationCatalog.cs":
            found.update(m.group(1) for m in CATALOG.finditer(source))
    for path in (ROOT / "src" / "PcChecker.Core").rglob("*.cs"):
        if "obj" in path.parts or "bin" in path.parts:
            continue
        for match in DESCRIPTION.finditer(path.read_text(encoding="utf-8")):
            found.add(unescape_csharp(match.group(1)))
    for folder in CORE_MARKED:
        for path in folder.rglob("*.cs"):
            if "obj" in path.parts or "bin" in path.parts:
                continue
            source = path.read_text(encoding="utf-8")
            for match in list(MARKED.finditer(source)) + list(TRANSLATOR_CALL.finditer(source)):
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
