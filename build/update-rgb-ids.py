#!/usr/bin/env python3
"""Rebuilds the RGB hardware ID lists the Lighting page uses to recognise RGB devices without OpenRGB running.

The lists are read from an OpenRGB source checkout (https://gitlab.com/CalcProgrammer1/OpenRGB): every USB/HID
device detector (REGISTER_HID_DETECTOR*) gives a vendor ID, product ID and product name, and every graphics card
detector (REGISTER_I2C_PCI_DETECTOR) gives the card's PCI vendor, device, subsystem vendor and subsystem device IDs.
Only those facts are copied; no OpenRGB code is. A few controllers that OpenRGB opens through libusb (no HID
detector line) are listed in EXTRA_USB, each taken from the #define in the named OpenRGB file.

Usage: python3 build/update-rgb-ids.py <path to OpenRGB checkout>
Writes src/PcChecker.Core/Lighting/Data/rgb-usb-ids.tsv and rgb-gpu-ids.tsv.
"""
import collections, datetime, os, pathlib, re, sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
OUT = ROOT / "src" / "PcChecker.Core" / "Lighting" / "Data"

# (vid, pid, name, family): libusb controllers, from the #defines in Controllers/<family>/*Detect.cpp.
EXTRA_USB = [
    (0x0CF2, 0x7750, "Lian Li Uni Hub", "LianLiController"),
    (0x1B1C, 0x0C12, "Corsair H150i PRO RGB", "CorsairHydroController"),
    (0x1B1C, 0x0C13, "Corsair H115i PRO RGB", "CorsairHydroController"),
    (0x1B1C, 0x0C15, "Corsair H100i PRO RGB", "CorsairHydroController"),
    (0x1B1C, 0x0C09, "Corsair H100i v2", "CorsairHydro2Controller"),
]

DEFINE = re.compile(r'^\s*#\s*define\s+([A-Za-z_]\w*)\s+\(?\s*(0[xX][0-9A-Fa-f]+|\d+|[A-Za-z_]\w*)\s*\)?\s*(//.*|/\*.*)?$')
CALL = re.compile(r'(REGISTER_(?:HID_DETECTOR\w*|HID_WRAPPED_DETECTOR\w*|DYNAMIC_HID_DETECTOR\w*|I2C_PCI_DETECTOR))\s*\((.*?)\)\s*;', re.S)


def read_defines(path):
    found = {}
    with open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            m = DEFINE.match(line)
            if m:
                found[m.group(1)] = m.group(2)
    return found


def main(checkout):
    controllers = os.path.join(checkout, "Controllers")
    pci_defs = read_defines(os.path.join(checkout, "pci_ids", "pci_ids.h"))
    everywhere = collections.defaultdict(set)
    for k, v in pci_defs.items():
        everywhere[k].add(v)
    per_family = collections.defaultdict(dict)
    sources = []
    for folder, _, names in os.walk(controllers):
        for name in names:
            if not name.endswith((".h", ".hpp", ".cpp")):
                continue
            path = os.path.join(folder, name)
            family = os.path.relpath(path, controllers).split(os.sep)[0]
            defs = read_defines(path)
            per_family[family].update(defs)
            for k, v in defs.items():
                everywhere[k].add(v)
            if name.endswith(".cpp"):
                sources.append((path, family))

    def resolve(token, family, depth=0):
        token = token.strip()
        if re.fullmatch(r"0[xX][0-9A-Fa-f]+", token):
            return int(token, 16)
        if re.fullmatch(r"\d+", token):
            return int(token)
        if depth > 5:
            return None
        if token in per_family[family]:
            return resolve(per_family[family][token], family, depth + 1)
        if token in pci_defs:
            return resolve(pci_defs[token], family, depth + 1)
        values = everywhere.get(token)
        if values and len(values) == 1:
            return resolve(next(iter(values)), family, depth + 1)
        return None

    usb, gpu = {}, {}
    for path, family in sorted(sources):
        text = open(path, encoding="utf-8", errors="replace").read()
        text = re.sub(r"/\*.*?\*/", "", text, flags=re.S)
        text = "\n".join(l for l in text.split("\n") if not l.strip().startswith("//"))
        for m in CALL.finditer(text):
            macro, args = m.group(1), m.group(2)
            if macro == "REGISTER_HID_DETECTOR_PU_ONLY":
                continue  # matches a HID usage page (LampArray), not a product ID; PC Checker checks that page itself
            literal = re.match(r'\s*"((?:[^"\\]|\\.)*)"\s*,(.*)', args, re.S)
            if literal:
                name, rest = literal.group(1).strip(), literal.group(2)
            else:
                ident = re.match(r"\s*([A-Za-z_]\w*)\s*,(.*)", args, re.S)
                named = ident and re.search(r"#\s*define\s+" + ident.group(1) + r'\s+"([^"]*)"', text)
                if not named:
                    continue
                name, rest = named.group(1).strip(), ident.group(2)
            parts = [p.strip() for p in rest.split(",")]
            if macro == "REGISTER_I2C_PCI_DETECTOR":
                ids = [resolve(p, family) for p in parts[1:5]] if len(parts) >= 5 else [None]
                if None not in ids:
                    gpu.setdefault(tuple(ids), (name, family))
            elif len(parts) >= 3:
                vid, pid = resolve(parts[1], family), resolve(parts[2], family)
                if vid is not None and pid is not None and vid <= 0xFFFF and pid <= 0xFFFF:
                    usb.setdefault((vid, pid), (name, family))

    for vid, pid, name, family in EXTRA_USB:
        usb.setdefault((vid, pid), (name, family))

    stamp = datetime.date.today().isoformat()
    OUT.mkdir(parents=True, exist_ok=True)
    with open(OUT / "rgb-usb-ids.tsv", "w", encoding="utf-8", newline="\n") as f:
        f.write(f"# RGB controllers OpenRGB recognises by USB/HID ID. Generated {stamp} by build/update-rgb-ids.py from OpenRGB's device detectors.\n")
        f.write("# vendor id\tproduct id\tproduct\tOpenRGB controller family\n")
        for (vid, pid), (name, family) in sorted(usb.items()):
            f.write(f"{vid:04X}\t{pid:04X}\t{name}\t{family}\n")
    with open(OUT / "rgb-gpu-ids.tsv", "w", encoding="utf-8", newline="\n") as f:
        f.write(f"# Graphics cards with RGB that OpenRGB recognises by PCI ID. Generated {stamp} by build/update-rgb-ids.py from OpenRGB's device detectors.\n")
        f.write("# vendor\tdevice\tsubsystem vendor\tsubsystem device\tproduct\tOpenRGB controller family\n")
        for (ven, dev, sven, sdev), (name, family) in sorted(gpu.items()):
            f.write(f"{ven:04X}\t{dev:04X}\t{sven:04X}\t{sdev:04X}\t{name}\t{family}\n")
    print(f"{len(usb)} USB/HID controllers, {len(gpu)} graphics cards written to {OUT}")


if __name__ == "__main__":
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    main(sys.argv[1])
