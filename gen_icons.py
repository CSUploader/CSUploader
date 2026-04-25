from PIL import Image, ImageDraw
import math, os

OUTPUT_DIR = r"E:\Projects\CSUploader\CSUploader\src\Properties\Images\Logo\candidates"
os.makedirs(OUTPUT_DIR, exist_ok=True)

SIZE = 512
PAD = 12

def make_gradient(size, top_color, bot_color):
    img = Image.new("RGBA", (size, size))
    for y in range(size):
        t = y / (size - 1)
        r = int(top_color[0] + t * (bot_color[0] - top_color[0]))
        g = int(top_color[1] + t * (bot_color[1] - top_color[1]))
        b = int(top_color[2] + t * (bot_color[2] - top_color[2]))
        for x in range(size):
            img.putpixel((x, y), (r, g, b, 255))
    return img

def rounded_rect_mask(size, pad, radius):
    mask = Image.new("L", (size, size), 0)
    d = ImageDraw.Draw(mask)
    d.rounded_rectangle([pad, pad, size-pad-1, size-pad-1], radius=radius, fill=255)
    return mask

def circle_mask(size, pad):
    mask = Image.new("L", (size, size), 0)
    d = ImageDraw.Draw(mask)
    d.ellipse([pad, pad, size-pad-1, size-pad-1], fill=255)
    return mask

def apply_mask(grad, mask):
    bg = Image.new("RGBA", grad.size, (0, 0, 0, 0))
    bg.paste(grad, mask=mask)
    return bg

# ============ SHAPE DRAWING FUNCTIONS ============

def draw_arrow_classic(draw, cx, cy, color, aw=180, ah=130, sw=70, sh=100):
    top = cy - (ah + sh) // 2 + 10
    pts = [
        (cx, top), (cx + aw//2, top + ah), (cx + sw//2, top + ah),
        (cx + sw//2, top + ah + sh), (cx - sw//2, top + ah + sh),
        (cx - sw//2, top + ah), (cx - aw//2, top + ah),
    ]
    draw.polygon(pts, fill=color)

def draw_arrow_rounded(draw, cx, cy, color, aw=180, ah=130, sw=70, sh=100):
    top = cy - (ah + sh) // 2 + 10
    draw.rounded_rectangle(
        [cx - sw//2, top + ah - 10, cx + sw//2, top + ah + sh],
        radius=sw//4, fill=color
    )
    pts = [(cx, top), (cx + aw//2, top + ah + 5), (cx - aw//2, top + ah + 5)]
    draw.polygon(pts, fill=color)

def draw_chevron_up(draw, cx, cy, color, w=200, h=100, thickness=45):
    top = cy - 40
    pts = [
        (cx, top), (cx + w//2, top + h), (cx + w//2 - thickness, top + h),
        (cx, top + thickness), (cx - w//2 + thickness, top + h), (cx - w//2, top + h),
    ]
    draw.polygon(pts, fill=color)

def draw_double_chevron(draw, cx, cy, color, w=180, h=80, thickness=35, gap=55):
    for off in [0, gap]:
        top = cy - 60 + off
        pts = [
            (cx, top), (cx + w//2, top + h), (cx + w//2 - thickness, top + h),
            (cx, top + thickness), (cx - w//2 + thickness, top + h), (cx - w//2, top + h),
        ]
        draw.polygon(pts, fill=color)

def draw_triple_chevron(draw, cx, cy, color, w=160, h=60, thickness=28, gap=45):
    for i in range(3):
        off = i * gap
        top = cy - 70 + off
        alpha = 255 - i * 50
        c = (color[0], color[1], color[2], alpha)
        pts = [
            (cx, top), (cx + w//2, top + h), (cx + w//2 - thickness, top + h),
            (cx, top + thickness), (cx - w//2 + thickness, top + h), (cx - w//2, top + h),
        ]
        draw.polygon(pts, fill=c)

def draw_box_arrow(draw, cx, cy, color):
    bw, bh = 180, 45
    by = cy + 55
    wall = 10
    draw.rounded_rectangle([cx-bw//2, by, cx-bw//2+wall, by+bh], radius=5, fill=color)
    draw.rounded_rectangle([cx+bw//2-wall, by, cx+bw//2, by+bh], radius=5, fill=color)
    draw.rounded_rectangle([cx-bw//2, by+bh-wall, cx+bw//2, by+bh], radius=5, fill=color)
    draw_arrow_classic(draw, cx, cy - 30, color, aw=140, ah=100, sw=55, sh=80)

def draw_bars_arrow(draw, cx, cy, color):
    bw = 160
    for i in range(3):
        by = cy + 55 + i * 26
        alpha = 255 - i * 50
        c = (color[0], color[1], color[2], alpha)
        draw.rounded_rectangle([cx-bw//2, by, cx+bw//2, by+10], radius=5, fill=c)
    draw_arrow_classic(draw, cx, cy - 25, color, aw=150, ah=110, sw=60, sh=80)

def draw_stacked_arrows(draw, cx, cy, color):
    for i, (aw, alpha) in enumerate([(180, 255), (140, 180), (100, 120)]):
        top = cy - 80 + i * 65
        c = (color[0], color[1], color[2], alpha)
        pts = [(cx, top), (cx + aw//2, top + 55), (cx - aw//2, top + 55)]
        draw.polygon(pts, fill=c)

def draw_arrow_line(draw, cx, cy, color):
    draw_arrow_classic(draw, cx, cy - 15, color, aw=160, ah=115, sw=60, sh=85)
    lw = 140
    draw.rounded_rectangle([cx-lw//2, cy+110, cx+lw//2, cy+120], radius=5, fill=color)

def draw_arrow_dots(draw, cx, cy, color):
    draw_arrow_classic(draw, cx, cy - 20, color, aw=160, ah=115, sw=60, sh=85)
    lw = 130
    draw.rounded_rectangle([cx-lw//2, cy+100, cx+lw//2, cy+108], radius=4, fill=color)
    for i in range(-2, 3):
        alpha = 220 - abs(i) * 35
        c = (color[0], color[1], color[2], alpha)
        dcx = cx + i * 30
        draw.ellipse([dcx-5, cy+125, dcx+5, cy+135], fill=c)

def draw_wide_arrow(draw, cx, cy, color):
    draw_arrow_classic(draw, cx, cy, color, aw=240, ah=140, sw=90, sh=90)

def draw_thin_arrow(draw, cx, cy, color):
    draw_arrow_classic(draw, cx, cy, color, aw=140, ah=150, sw=44, sh=110)

def draw_fat_chevron(draw, cx, cy, color):
    draw_chevron_up(draw, cx, cy, color, w=240, h=120, thickness=65)

def draw_cloud_upload(draw, cx, cy, color):
    cr = 55
    draw.ellipse([cx-80, cy-60, cx-80+cr*2, cy-60+cr*2], fill=color)
    draw.ellipse([cx+80-cr*2, cy-60, cx+80, cy-60+cr*2], fill=color)
    draw.ellipse([cx-cr-10, cy-95, cx+cr+10, cy-95+cr*2+10], fill=color)
    draw.rectangle([cx-80, cy-20, cx+80, cy+10], fill=color)
    aw, ah, sw, sh = 100, 70, 38, 50
    atop = cy + 30
    pts = [
        (cx, atop), (cx + aw//2, atop + ah), (cx + sw//2, atop + ah),
        (cx + sw//2, atop + ah + sh), (cx - sw//2, atop + ah + sh),
        (cx - sw//2, atop + ah), (cx - aw//2, atop + ah),
    ]
    flipped = [(x, cy - (y - cy) + 15) for x, y in pts]
    draw.polygon(flipped, fill=color)

def draw_paper_plane(draw, cx, cy, color):
    pts = [(cx - 110, cy + 90), (cx + 130, cy - 10), (cx - 10, cy - 110)]
    draw.polygon(pts, fill=color)
    c2 = (color[0], color[1], color[2], 180)
    pts2 = [(cx - 110, cy + 90), (cx + 130, cy - 10), (cx + 5, cy + 20)]
    draw.polygon(pts2, fill=c2)

def draw_shield_arrow(draw, cx, cy, color):
    sw, sh = 190, 240
    top = cy - sh//2
    draw.rounded_rectangle([cx-sw//2, top, cx+sw//2, top+sh-60], radius=20, fill=color)
    pts = [(cx - sw//2, top+sh-80), (cx, top+sh), (cx+sw//2, top+sh-80)]
    draw.polygon(pts, fill=color)
    bg_c = (0, 0, 0, 0)
    draw_arrow_classic(draw, cx, cy - 10, bg_c, aw=100, ah=75, sw=38, sh=55)

def draw_circle_arrow(draw, cx, cy, color):
    r = 145
    thick = 16
    draw.ellipse([cx-r, cy-r, cx+r, cy+r], fill=color)
    inner = (0, 0, 0, 0)
    draw.ellipse([cx-r+thick, cy-r+thick, cx+r-thick, cy+r-thick], fill=inner)
    draw_arrow_classic(draw, cx, cy, color, aw=130, ah=95, sw=50, sh=70)

def draw_hexagon_arrow(draw, cx, cy, color):
    r = 160
    pts = []
    for i in range(6):
        angle = math.radians(60 * i - 90)
        pts.append((cx + r * math.cos(angle), cy + r * math.sin(angle)))
    draw.polygon(pts, fill=color)
    bg_c = (0, 0, 0, 0)
    draw_arrow_classic(draw, cx, cy, bg_c, aw=110, ah=85, sw=42, sh=60)

def draw_diamond_arrow(draw, cx, cy, color):
    r = 170
    pts = [(cx, cy-r), (cx+r, cy), (cx, cy+r), (cx-r, cy)]
    draw.polygon(pts, fill=color)
    bg_c = (0, 0, 0, 0)
    draw_arrow_classic(draw, cx, cy, bg_c, aw=100, ah=75, sw=38, sh=55)

# ============ PALETTES ============
palettes = [
    ("deep_blue",       (26, 45, 90),    (74, 140, 232)),
    ("navy_cyan",       (15, 25, 60),    (40, 180, 220)),
    ("ocean_teal",      (10, 50, 70),    (40, 200, 180)),
    ("midnight_purple", (30, 20, 60),    (120, 80, 200)),
    ("violet_pink",     (60, 20, 80),    (180, 80, 200)),
    ("dark_emerald",    (10, 40, 30),    (40, 180, 100)),
    ("forest_lime",     (20, 50, 30),    (80, 200, 80)),
    ("charcoal_orange", (40, 35, 30),    (230, 130, 50)),
    ("slate_red",       (50, 25, 30),    (220, 70, 70)),
    ("graphite_gold",   (35, 35, 35),    (220, 180, 50)),
    ("steel_blue",      (35, 45, 65),    (100, 160, 230)),
    ("indigo_sky",      (25, 20, 70),    (80, 140, 240)),
    ("royal_azure",     (20, 30, 80),    (50, 120, 240)),
    ("dark_teal",       (10, 35, 45),    (20, 160, 160)),
    ("warm_navy",       (30, 35, 55),    (70, 130, 200)),
]

shapes = [
    ("classic",        draw_arrow_classic),
    ("rounded",        draw_arrow_rounded),
    ("chevron",        draw_chevron_up),
    ("double_chev",    draw_double_chevron),
    ("triple_chev",    draw_triple_chevron),
    ("box_arrow",      draw_box_arrow),
    ("bars_arrow",     draw_bars_arrow),
    ("stacked",        draw_stacked_arrows),
    ("arrow_line",     draw_arrow_line),
    ("arrow_dots",     draw_arrow_dots),
    ("wide_arrow",     draw_wide_arrow),
    ("thin_arrow",     draw_thin_arrow),
    ("fat_chevron",    draw_fat_chevron),
    ("cloud_upload",   draw_cloud_upload),
    ("paper_plane",    draw_paper_plane),
    ("circle_arrow",   draw_circle_arrow),
]

mask_types = ["rounded_rect", "circle"]

# Generate 50 curated combinations
combos = [
    # Classic arrows across key palettes (rounded rect)
    (0, 0, 0), (1, 0, 0), (2, 0, 0), (4, 0, 0), (10, 0, 0),
    # Classic arrows (circle)
    (0, 0, 1), (3, 0, 1), (5, 0, 1),
    # Rounded arrows
    (0, 1, 0), (1, 1, 0), (10, 1, 0), (12, 1, 0), (0, 1, 1),
    # Chevrons
    (0, 2, 0), (1, 2, 0), (5, 2, 0), (0, 2, 1),
    # Double chevrons
    (0, 3, 0), (3, 3, 0), (10, 3, 0), (0, 3, 1),
    # Triple chevrons
    (0, 4, 0), (1, 4, 0), (12, 4, 0),
    # Box arrow
    (0, 5, 0), (1, 5, 0), (10, 5, 0),
    # Bars arrow
    (0, 6, 0), (2, 6, 0), (10, 6, 0),
    # Stacked triangles
    (0, 7, 0), (3, 7, 0), (5, 7, 0),
    # Arrow + line
    (0, 8, 0), (10, 8, 0), (14, 8, 0), (0, 8, 1),
    # Arrow + dots
    (0, 9, 0), (1, 9, 0), (10, 9, 0),
    # Wide arrow
    (0, 10, 0), (7, 10, 0),
    # Thin arrow
    (0, 11, 0), (12, 11, 0),
    # Fat chevron
    (0, 12, 0), (5, 12, 0),
    # Cloud upload
    (0, 13, 0), (1, 13, 0), (10, 13, 0),
    # Paper plane
    (0, 14, 0), (1, 14, 1),
    # Circle arrow
    (0, 15, 0), (3, 15, 0),
]

combos = combos[:50]

for idx, (pi, si, mi) in enumerate(combos):
    pname, top, bot = palettes[pi]
    sname, sfn = shapes[si]
    mtype = mask_types[mi]

    img = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    grad = make_gradient(SIZE, top, bot)
    if mtype == "rounded_rect":
        mask = rounded_rect_mask(SIZE, PAD, 100)
    else:
        mask = circle_mask(SIZE, PAD)
    bg = apply_mask(grad, mask)
    img = Image.alpha_composite(img, bg)
    draw = ImageDraw.Draw(img)

    cx, cy = SIZE // 2, SIZE // 2
    white = (255, 255, 255, 240)
    sfn(draw, cx, cy, white)

    fname = f"{idx+1:02d}_{pname}_{sname}_{mtype}.png"
    img.save(os.path.join(OUTPUT_DIR, fname), "PNG")

print(f"Generated {len(combos)} icons in {OUTPUT_DIR}")
