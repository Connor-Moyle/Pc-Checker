"""Renders the PC Checker mark to Assets/logo.png (512 px) and a multi-size Assets/icon.ico."""
from pathlib import Path
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "src" / "PcChecker.App" / "Assets"
SCALE = 4  # supersample for smooth edges


def render(size: int) -> Image.Image:
    s = size * SCALE
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    u = s / 512.0

    # Rounded dark-blue square with a subtle vertical gradient.
    pad, radius = 32 * u, 96 * u
    base = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    gd = ImageDraw.Draw(base)
    top, bottom = (30, 58, 138), (15, 31, 77)
    for y in range(int(pad), int(s - pad)):
        t = (y - pad) / max(1, (s - 2 * pad))
        c = tuple(int(top[i] + (bottom[i] - top[i]) * t) for i in range(3)) + (255,)
        gd.line([(pad, y), (s - pad, y)], fill=c)
    mask = Image.new("L", (s, s), 0)
    ImageDraw.Draw(mask).rounded_rectangle([pad, pad, s - pad, s - pad], radius=radius, fill=255)
    img.paste(base, (0, 0), mask)

    # Pulse line.
    pts = [(88, 268), (168, 268), (204, 180), (252, 352), (300, 224), (332, 268), (424, 268)]
    pts = [(x * u, y * u) for x, y in pts]
    w = max(2, int(30 * u))
    d.line(pts, fill=(103, 232, 249, 255), width=w, joint="curve")
    for x, y in (pts[0], pts[-1]):
        d.ellipse([x - w / 2, y - w / 2, x + w / 2, y + w / 2], fill=(103, 232, 249, 255))

    # Check badge.
    cx, cy, r = 392 * u, 388 * u, 56 * u
    d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=(34, 197, 94, 255))
    cw = max(2, int(16 * u))
    check = [(364 * u, 388 * u), (384 * u, 408 * u), (420 * u, 368 * u)]
    d.line(check, fill=(255, 255, 255, 255), width=cw, joint="curve")
    for x, y in (check[0], check[-1]):
        d.ellipse([x - cw / 2, y - cw / 2, x + cw / 2, y + cw / 2], fill=(255, 255, 255, 255))

    return img.resize((size, size), Image.LANCZOS)


def main() -> None:
    ASSETS.mkdir(parents=True, exist_ok=True)
    logo = render(512)
    logo.save(ASSETS / "logo.png", "PNG")
    sizes = [256, 128, 64, 48, 32, 16]
    frames = [render(sz) for sz in sizes]
    frames[0].save(ASSETS / "icon.ico", format="ICO", sizes=[(sz, sz) for sz in sizes], append_images=frames[1:])
    print("wrote", ASSETS / "logo.png", "and", ASSETS / "icon.ico")


if __name__ == "__main__":
    main()
