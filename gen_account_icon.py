"""Generate a generic 'Accounts' icon for the Settings sidebar.

Renders a simple person silhouette (head + shoulders) at 64x64, supersampled
for clean anti-aliasing. Output: src/Properties/Images/account.png
"""

import os
from PIL import Image, ImageDraw

OUT = r"E:\Projects\CSUploader\CSUploader\src\Properties\Images\account.png"

# Render at 4x and downsample for crisp edges
SCALE = 4
SIZE = 64
S = SIZE * SCALE

# Soft slate gray that reads well on both light and dark backgrounds
FG = (90, 110, 135, 255)


def draw_person(draw: ImageDraw.ImageDraw) -> None:
    cx = S // 2

    # Head: circle in upper third
    head_r = int(S * 0.18)
    head_cy = int(S * 0.30)
    draw.ellipse(
        [(cx - head_r, head_cy - head_r), (cx + head_r, head_cy + head_r)],
        fill=FG,
    )

    # Shoulders: rounded "U" cut at the bottom (top half of an ellipse)
    body_w = int(S * 0.72)
    body_h = int(S * 0.68)
    body_cy = int(S * 0.92)
    # Draw an ellipse and clip the bottom — easiest via a second image masked.
    body_left = cx - body_w // 2
    body_right = cx + body_w // 2
    body_top = body_cy - body_h // 2
    body_bottom = body_cy + body_h // 2
    draw.ellipse([(body_left, body_top), (body_right, body_bottom)], fill=FG)


def main() -> None:
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)
    draw_person(draw)

    # Crop the bottom half of the body ellipse off so it looks like shoulders.
    crop_y = int(S * 0.78)
    cropped = img.crop((0, 0, S, crop_y))
    final = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    final.paste(cropped, (0, 0), cropped)

    final = final.resize((SIZE, SIZE), Image.LANCZOS)
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    final.save(OUT, "PNG")
    print(f"Wrote {OUT} ({SIZE}x{SIZE})")


if __name__ == "__main__":
    main()
