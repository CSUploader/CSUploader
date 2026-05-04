"""Fetch hoster favicons to use as their icons in the UI.

For each hoster registered in FileHosterClient.cs, tries (in order):
  1. https://{domain}/apple-touch-icon.png         (~180x180, best quality)
  2. https://{domain}/apple-touch-icon-precomposed.png
  3. https://{domain}/favicon.ico                  (smaller, last resort)
  4. https://www.google.com/s2/favicons?domain={domain}&sz=64  (Google fallback)

Resizes to 64x64 PNG and writes to src/Properties/Images/FileHosters/filehoster_<lower>.png.
Skips hosters whose icon already exists.
"""

import io
import os
import re
import sys
import urllib.request
from pathlib import Path
from PIL import Image

ROOT = Path(r"E:\Projects\CSUploader\CSUploader")
OUTPUT_DIR = ROOT / "src" / "Properties" / "Images" / "FileHosters"
SOURCE = ROOT / "src" / "Upload" / "FileHosterClient.cs"

USER_AGENT = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"
TARGET_SIZE = 64
TIMEOUT = 10


def parse_hosters() -> dict[str, str]:
    """Read FileHosterClient.cs and return name -> domain dict."""
    text = SOURCE.read_text(encoding="utf-8")
    pattern = re.compile(r'\{\s*"([^"]+)"\s*,\s*"([^"]+)"\s*\}')
    return dict(pattern.findall(text))


_IMAGE_MAGIC = (b"\x89PNG", b"\xff\xd8\xff", b"GIF8", b"\x00\x00\x01\x00", b"\x00\x00\x02\x00", b"RIFF")


def is_image_bytes(data: bytes) -> bool:
    """True if `data` starts with a known image-format magic number."""
    return any(data.startswith(m) for m in _IMAGE_MAGIC)


def fetch(url: str) -> bytes | None:
    req = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
    try:
        with urllib.request.urlopen(req, timeout=TIMEOUT) as resp:
            data = resp.read()
            if data and len(data) > 32 and is_image_bytes(data):
                return data
            if data and not is_image_bytes(data):
                print(f"    [debug] {url}: not an image (got {data[:8]!r})")
    except Exception as ex:
        print(f"    [debug] {url}: {type(ex).__name__}: {ex}")
    return None


def fetch_icon(domain: str) -> bytes | None:
    """Try multiple icon sources and return the first that succeeds."""
    bare = domain.removeprefix("www.")
    candidates = [
        # Direct from the hoster
        f"https://{domain}/apple-touch-icon.png",
        f"https://{domain}/apple-touch-icon-precomposed.png",
        f"https://{bare}/apple-touch-icon.png",
        f"https://{domain}/favicon.ico",
        f"https://{bare}/favicon.ico",
        # icon.horse - reliable third-party favicon proxy
        f"https://icon.horse/icon/{bare}?size=large",
        # DuckDuckGo's favicon service
        f"https://icons.duckduckgo.com/ip3/{bare}.ico",
        # Google's deprecated-but-still-working endpoint
        f"https://www.google.com/s2/favicons?domain={bare}&sz=64",
    ]
    for url in candidates:
        data = fetch(url)
        if data is not None:
            print(f"    -> {url}")
            return data
    return None


def save_as_png(data: bytes, output_path: Path) -> bool:
    """Decode whatever format the bytes are and write a 64x64 PNG."""
    try:
        img = Image.open(io.BytesIO(data))
    except Exception as ex:
        print(f"    [decode] {ex}")
        return False

    # ICOs may have multiple sizes — pick the largest
    if img.format == "ICO":
        sizes = img.info.get("sizes") or [(img.size[0], img.size[1])]
        biggest = max(sizes, key=lambda s: s[0] * s[1])
        img.size = biggest
        img.load()

    img = img.convert("RGBA")
    if img.size != (TARGET_SIZE, TARGET_SIZE):
        img = img.resize((TARGET_SIZE, TARGET_SIZE), Image.LANCZOS)
    img.save(output_path, "PNG")
    return True


def make_letter_badge(name: str, output_path: Path) -> None:
    """Generate a colored letter-badge placeholder. Color seeded from the name so it's stable."""
    from PIL import ImageDraw, ImageFont

    # Stable hue from name
    h = sum(ord(c) for c in name)
    palette = [
        (208, 80, 80), (220, 130, 60), (200, 170, 60), (90, 170, 90),
        (60, 150, 180), (80, 110, 200), (140, 90, 190), (200, 90, 150),
    ]
    bg = palette[h % len(palette)]

    img = Image.new("RGBA", (TARGET_SIZE, TARGET_SIZE), bg + (255,))
    draw = ImageDraw.Draw(img)

    letter = name[0].upper()
    try:
        font = ImageFont.truetype("seguibl.ttf", 40)
    except OSError:
        font = ImageFont.load_default()

    # Center the letter
    bbox = draw.textbbox((0, 0), letter, font=font)
    w, h_ = bbox[2] - bbox[0], bbox[3] - bbox[1]
    x = (TARGET_SIZE - w) // 2 - bbox[0]
    y = (TARGET_SIZE - h_) // 2 - bbox[1]
    draw.text((x, y), letter, fill=(255, 255, 255, 255), font=font)

    img.save(output_path, "PNG")


def main() -> int:
    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)
    hosters = parse_hosters()
    print(f"Found {len(hosters)} hosters in {SOURCE.name}\n")

    fetched, skipped, failed, generated = [], [], [], []
    for name, domain in hosters.items():
        slug = name.lower()
        out = OUTPUT_DIR / f"filehoster_{slug}.png"

        if out.exists():
            print(f"[skip] {name} (icon exists)")
            skipped.append(name)
            continue

        print(f"[fetch] {name}  <-  {domain}")
        data = fetch_icon(domain)
        if data is not None and save_as_png(data, out):
            print(f"    saved -> {out.name}")
            fetched.append(name)
        else:
            print(f"    fetch failed - generating placeholder")
            make_letter_badge(name, out)
            generated.append(name)

    print(f"\n=== Summary ===")
    print(f"  Fetched:    {len(fetched)} ({', '.join(fetched) if fetched else '-'})")
    print(f"  Skipped:    {len(skipped)} ({', '.join(skipped) if skipped else '-'})")
    print(f"  Generated:  {len(generated)} ({', '.join(generated) if generated else '-'})")

    return 0


if __name__ == "__main__":
    sys.exit(main())
