from PIL import Image, ImageDraw, ImageFilter
import math, os

OUTPUT_DIR = r"E:\Projects\CSUploader\CSUploader\src\Properties\Images\Logo\candidates_v2"
os.makedirs(OUTPUT_DIR, exist_ok=True)

SIZE = 512
PAD = 12

def make_base(size, pad, radius, top_c, bot_c, radial=False):
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    mask = Image.new("L", (size, size), 0)
    d = ImageDraw.Draw(mask)
    d.rounded_rectangle([pad, pad, size-pad-1, size-pad-1], radius=radius, fill=255)

    grad = Image.new("RGBA", (size, size))
    cx, cy = size // 2, size // 2
    for y in range(size):
        for x in range(size):
            if radial:
                dist = math.sqrt((x - cx)**2 + (y - cy)**2) / (size * 0.7)
                t = min(dist, 1.0)
            else:
                t = y / (size - 1)
            r = int(top_c[0] + t * (bot_c[0] - top_c[0]))
            g = int(top_c[1] + t * (bot_c[1] - top_c[1]))
            b = int(top_c[2] + t * (bot_c[2] - top_c[2]))
            grad.putpixel((x, y), (r, g, b, 255))

    bg = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    bg.paste(grad, mask=mask)
    return bg

def add_glow(img, shape_img, color, radius=15):
    glow = Image.new("RGBA", img.size, (0, 0, 0, 0))
    glow_layer = Image.new("RGBA", img.size, (0, 0, 0, 0))
    glow_layer.paste(Image.new("RGBA", img.size, color), mask=shape_img.split()[3])
    glow_layer = glow_layer.filter(ImageFilter.GaussianBlur(radius))
    return Image.alpha_composite(img, glow_layer)

def draw_on_layer(size, draw_fn):
    layer = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    draw_fn(d)
    return layer


# ============ CREATIVE ICON DESIGNS ============

def icon_01_layered_arrows(base):
    """Three overlapping translucent arrows creating depth"""
    img = base.copy()
    cx, cy = SIZE // 2, SIZE // 2
    for i, (offset, alpha, scale) in enumerate([
        (50, 60, 1.3), (25, 100, 1.0), (0, 220, 0.7)
    ]):
        layer = draw_on_layer(SIZE, lambda d, o=offset, s=scale: d.polygon([
            (cx, cy - 120 + o),
            (cx + int(90*s), cy + 20 + o),
            (cx + int(35*s), cy + 20 + o),
            (cx + int(35*s), cy + 100 + o),
            (cx - int(35*s), cy + 100 + o),
            (cx - int(35*s), cy + 20 + o),
            (cx - int(90*s), cy + 20 + o),
        ], fill=(255, 255, 255, alpha)))
        img = Image.alpha_composite(img, layer)
    return img

def icon_02_ribbon_arrow(base):
    """Flowing ribbon that forms an upward arrow"""
    img = base.copy()
    d = ImageDraw.Draw(img)
    cx, cy = SIZE // 2, SIZE // 2

    # Ribbon body - S-curve
    points_left = []
    points_right = []
    width = 35
    for i in range(100):
        t = i / 99.0
        y = cy + 120 - t * 280
        x_offset = math.sin(t * math.pi * 1.5) * 40
        points_left.append((cx + x_offset - width, y))
        points_right.append((cx + x_offset + width, y))

    points_right.reverse()
    d.polygon(points_left + points_right, fill=(255, 255, 255, 200))

    # Arrowhead at top
    top_y = cy - 160
    tip_x = cx + math.sin(1.5 * math.pi) * 40
    d.polygon([
        (tip_x, top_y - 20),
        (tip_x + 70, top_y + 60),
        (tip_x - 70, top_y + 60),
    ], fill=(255, 255, 255, 240))
    return img

def icon_03_faceted_arrow(base):
    """Geometric faceted/crystal arrow with light and dark faces"""
    img = base.copy()
    d = ImageDraw.Draw(img)
    cx, cy = SIZE // 2, SIZE // 2

    # Left face of arrowhead (darker)
    d.polygon([
        (cx, cy - 140),
        (cx - 100, cy + 10),
        (cx, cy - 20),
    ], fill=(200, 220, 255, 200))

    # Right face of arrowhead (lighter)
    d.polygon([
        (cx, cy - 140),
        (cx + 100, cy + 10),
        (cx, cy - 20),
    ], fill=(255, 255, 255, 240))

    # Left face of shaft
    d.polygon([
        (cx - 38, cy + 10),
        (cx, cy - 20),
        (cx, cy + 120),
        (cx - 38, cy + 120),
    ], fill=(200, 220, 255, 180))

    # Right face of shaft
    d.polygon([
        (cx + 38, cy + 10),
        (cx, cy - 20),
        (cx, cy + 120),
        (cx + 38, cy + 120),
    ], fill=(255, 255, 255, 220))
    return img

def icon_04_orbital_arrow(base):
    """Arrow with circular orbit ring around it"""
    img = base.copy()
    cx, cy = SIZE // 2, SIZE // 2

    # Draw orbit ring as ellipse
    ring = draw_on_layer(SIZE, lambda d: None)
    rd = ImageDraw.Draw(ring)

    # Orbit ellipse - draw thick by layering
    for t in range(720):
        angle = math.radians(t / 2)
        rx, ry = 160, 55
        x = cx + rx * math.cos(angle)
        y = cy + 30 + ry * math.sin(angle)
        # Only draw back half behind arrow
        if t < 360:
            rd.ellipse([x-4, y-4, x+4, y+4], fill=(255, 255, 255, 80))

    img = Image.alpha_composite(img, ring)

    # Arrow
    d = ImageDraw.Draw(img)
    d.polygon([
        (cx, cy - 130),
        (cx + 80, cy + 10),
        (cx + 30, cy + 10),
        (cx + 30, cy + 100),
        (cx - 30, cy + 100),
        (cx - 30, cy + 10),
        (cx - 80, cy + 10),
    ], fill=(255, 255, 255, 235))

    # Front half of orbit
    ring2 = draw_on_layer(SIZE, lambda d: None)
    rd2 = ImageDraw.Draw(ring2)
    for t in range(360, 720):
        angle = math.radians(t / 2)
        rx, ry = 160, 55
        x = cx + rx * math.cos(angle)
        y = cy + 30 + ry * math.sin(angle)
        rd2.ellipse([x-4, y-4, x+4, y+4], fill=(255, 255, 255, 160))
    img = Image.alpha_composite(img, ring2)
    return img

def icon_05_rising_particles(base):
    """Arrow dissolving into rising particles at the top"""
    img = base.copy()
    d = ImageDraw.Draw(img)
    cx, cy = SIZE // 2, SIZE // 2

    # Solid shaft
    d.rounded_rectangle([cx-35, cy-20, cx+35, cy+120], radius=10, fill=(255,255,255,230))

    # Arrow head, partially solid
    d.polygon([
        (cx, cy - 80),
        (cx + 85, cy + 10),
        (cx - 85, cy + 10),
    ], fill=(255, 255, 255, 220))

    # Particles rising from top
    import random
    random.seed(42)
    for i in range(45):
        t = random.random()
        spread = t * 100
        px = cx + random.uniform(-spread, spread)
        py = cy - 80 - t * 150
        size = random.uniform(3, 10) * (1 - t * 0.6)
        alpha = int(255 * (1 - t * 0.8))
        d.ellipse([px-size, py-size, px+size, py+size], fill=(255, 255, 255, alpha))
    return img

def icon_06_neon_outline(base):
    """Neon-glow outlined arrow on dark background"""
    img = base.copy()
    cx, cy = SIZE // 2, SIZE // 2

    arrow_pts = [
        (cx, cy - 130),
        (cx + 90, cy + 10),
        (cx + 35, cy + 10),
        (cx + 35, cy + 110),
        (cx - 35, cy + 110),
        (cx - 35, cy + 10),
        (cx - 90, cy + 10),
    ]

    # Draw glow layers (progressively larger and more transparent)
    for blur_r, alpha in [(20, 40), (12, 60), (6, 100), (3, 160)]:
        glow = draw_on_layer(SIZE, lambda d, pts=arrow_pts, a=alpha:
            d.polygon(pts, outline=(120, 200, 255, a), width=4))
        glow = glow.filter(ImageFilter.GaussianBlur(blur_r))
        img = Image.alpha_composite(img, glow)

    # Sharp outline
    outline = draw_on_layer(SIZE, lambda d:
        d.polygon(arrow_pts, outline=(180, 230, 255, 255), width=3))
    img = Image.alpha_composite(img, outline)
    return img

def icon_07_speed_lines(base):
    """Arrow with speed/motion lines trailing behind"""
    img = base.copy()
    d = ImageDraw.Draw(img)
    cx, cy = SIZE // 2, SIZE // 2

    # Speed lines
    for i, (x_off, length, alpha) in enumerate([
        (-80, 80, 80), (-50, 100, 100), (-20, 60, 70),
        (20, 70, 90), (50, 90, 100), (80, 75, 80),
    ]):
        x = cx + x_off
        y_start = cy + 100
        d.rounded_rectangle(
            [x-2, y_start, x+2, y_start + length],
            radius=2, fill=(255, 255, 255, alpha)
        )

    # Main arrow
    d.polygon([
        (cx, cy - 130),
        (cx + 90, cy + 10),
        (cx + 35, cy + 10),
        (cx + 35, cy + 90),
        (cx - 35, cy + 90),
        (cx - 35, cy + 10),
        (cx - 90, cy + 10),
    ], fill=(255, 255, 255, 240))
    return img

def icon_08_gradient_arrow(base):
    """Arrow with internal gradient from transparent to white"""
    img = base.copy()
    cx, cy = SIZE // 2, SIZE // 2

    # Build arrow with vertical gradient fill
    arrow = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    ad = ImageDraw.Draw(arrow)
    ad.polygon([
        (cx, cy - 140),
        (cx + 100, cy + 20),
        (cx + 40, cy + 20),
        (cx + 40, cy + 130),
        (cx - 40, cy + 130),
        (cx - 40, cy + 20),
        (cx - 100, cy + 20),
    ], fill=(255, 255, 255, 255))

    # Apply vertical alpha gradient to the arrow
    grad_mask = Image.new("L", (SIZE, SIZE), 0)
    for y in range(SIZE):
        # Bottom = transparent, top = opaque
        t = 1.0 - (y / SIZE)
        alpha = int(80 + t * 175)
        for x in range(SIZE):
            if arrow.getpixel((x, y))[3] > 0:
                grad_mask.putpixel((x, y), alpha)

    arrow.putalpha(grad_mask)
    img = Image.alpha_composite(img, arrow)
    return img

def icon_09_cs_monogram(base):
    """Stylized CS letters forming an abstract upload shape"""
    img = base.copy()
    d = ImageDraw.Draw(img)
    cx, cy = SIZE // 2, SIZE // 2

    # "C" shape - arc
    for angle_deg in range(45, 315):
        angle = math.radians(angle_deg)
        for r in range(70, 90):
            x = cx - 30 + r * math.cos(angle)
            y = cy + r * math.sin(angle)
            if 0 <= x < SIZE and 0 <= y < SIZE:
                d.point((x, y), fill=(255, 255, 255, 220))

    # "S" as a lightning/flow shape
    pts = [
        (cx + 40, cy - 80),
        (cx + 80, cy - 80),
        (cx + 20, cy),
        (cx + 60, cy),
        (cx, cy + 80),
        (cx + 40, cy + 10),
        (cx, cy + 10),
        (cx + 60, cy - 70),
    ]
    d.polygon(pts, fill=(255, 255, 255, 230))

    # Upward accent
    d.polygon([
        (cx, cy - 140),
        (cx + 30, cy - 100),
        (cx - 30, cy - 100),
    ], fill=(255, 255, 255, 200))
    return img

def icon_10_concentric_rings(base):
    """Concentric broken rings with arrow emerging from center"""
    img = base.copy()
    cx, cy = SIZE // 2, SIZE // 2 + 20

    # Broken concentric rings
    for r, thickness, alpha, gap_start, gap_end in [
        (150, 8, 60, 240, 300),
        (120, 10, 90, 250, 290),
        (90, 12, 120, 255, 285),
    ]:
        ring = draw_on_layer(SIZE, lambda d, r=r, t=thickness, a=alpha, gs=gap_start, ge=gap_end: [
            d.arc([cx-r, cy-r, cx+r, cy+r], gs, ge + 360 - (ge - gs) + gs, fill=(255, 255, 255, a), width=t)
        ])
        # Draw arc segments
        rd = ImageDraw.Draw(ring)
        for angle_deg in range(0, 360):
            if gap_start <= angle_deg <= gap_end:
                continue
            angle = math.radians(angle_deg)
            for dr in range(-thickness//2, thickness//2):
                x = cx + (r + dr) * math.cos(angle)
                y = cy + (r + dr) * math.sin(angle)
                if 0 <= x < SIZE and 0 <= y < SIZE:
                    rd.point((x, y), fill=(255, 255, 255, alpha))
        img = Image.alpha_composite(img, ring)

    # Central arrow
    d = ImageDraw.Draw(img)
    d.polygon([
        (cx, cy - 120),
        (cx + 55, cy - 30),
        (cx + 22, cy - 30),
        (cx + 22, cy + 40),
        (cx - 22, cy + 40),
        (cx - 22, cy - 30),
        (cx - 55, cy - 30),
    ], fill=(255, 255, 255, 240))
    return img

def icon_11_shield_upload(base):
    """Shield shape with upload arrow cutout"""
    img = base.copy()
    cx, cy = SIZE // 2, SIZE // 2

    # Shield
    shield = draw_on_layer(SIZE, lambda d: None)
    sd = ImageDraw.Draw(shield)
    # Shield body
    sd.rounded_rectangle([cx-110, cy-130, cx+110, cy+50], radius=20, fill=(255,255,255,220))
    # Shield point
    sd.polygon([(cx-110, cy+30), (cx, cy+150), (cx+110, cy+30)], fill=(255,255,255,220))

    # Cut out arrow shape (use base bg color approximation)
    sd.polygon([
        (cx, cy - 90),
        (cx + 65, cy),
        (cx + 25, cy),
        (cx + 25, cy + 60),
        (cx - 25, cy + 60),
        (cx - 25, cy),
        (cx - 65, cy),
    ], fill=(0, 0, 0, 0))

    img = Image.alpha_composite(img, shield)
    return img

def icon_12_wave_arrow(base):
    """Upward arrow with flowing wave patterns"""
    img = base.copy()
    cx, cy = SIZE // 2, SIZE // 2

    # Flowing waves at bottom
    for i in range(4):
        wave = draw_on_layer(SIZE, lambda d: None)
        wd = ImageDraw.Draw(wave)
        y_base = cy + 80 + i * 25
        alpha = 150 - i * 30
        points = []
        for x in range(cx - 140, cx + 141):
            y = y_base + math.sin((x - cx) / 30 + i * 0.8) * 8
            points.append((x, y))
        for x in range(cx + 140, cx - 141, -1):
            y = y_base + 6 + math.sin((x - cx) / 30 + i * 0.8) * 8
            points.append((x, y))
        wd.polygon(points, fill=(255, 255, 255, alpha))
        img = Image.alpha_composite(img, wave)

    # Arrow
    d = ImageDraw.Draw(img)
    d.polygon([
        (cx, cy - 130),
        (cx + 85, cy + 10),
        (cx + 30, cy + 10),
        (cx + 30, cy + 70),
        (cx - 30, cy + 70),
        (cx - 30, cy + 10),
        (cx - 85, cy + 10),
    ], fill=(255, 255, 255, 235))
    return img

def icon_13_abstract_a(base):
    """Abstract letter 'A' that also reads as an upward arrow"""
    img = base.copy()
    d = ImageDraw.Draw(img)
    cx, cy = SIZE // 2, SIZE // 2

    # Outer A/arrow shape
    d.polygon([
        (cx, cy - 150),
        (cx + 120, cy + 130),
        (cx + 75, cy + 130),
        (cx + 40, cy + 40),
        (cx - 40, cy + 40),
        (cx - 75, cy + 130),
        (cx - 120, cy + 130),
    ], fill=(255, 255, 255, 230))

    # Crossbar
    d.rectangle([cx - 55, cy + 15, cx + 55, cy + 40], fill=(0, 0, 0, 0))

    # Inner cutout to make it read as "A"
    d.polygon([
        (cx, cy - 60),
        (cx + 30, cy + 15),
        (cx - 30, cy + 15),
    ], fill=(0, 0, 0, 0))
    return img

def icon_14_double_layer(base_top, base_bot):
    """Split design - two tones with arrow bridging them"""
    img = base_bot.copy()
    cx, cy = SIZE // 2, SIZE // 2

    # Top half overlay
    top_half = base_top.copy()
    mask = Image.new("L", (SIZE, SIZE), 0)
    d = ImageDraw.Draw(mask)
    d.rectangle([0, 0, SIZE, cy], fill=255)
    top_overlay = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    top_overlay.paste(top_half, mask=mask)
    img = Image.alpha_composite(img, top_overlay)

    # Divider line
    dd = ImageDraw.Draw(img)
    dd.line([(PAD + 20, cy), (SIZE - PAD - 20, cy)], fill=(255, 255, 255, 40), width=2)

    # Arrow bridging both halves
    dd.polygon([
        (cx, cy - 130),
        (cx + 85, cy - 10),
        (cx + 32, cy - 10),
        (cx + 32, cy + 110),
        (cx - 32, cy + 110),
        (cx - 32, cy - 10),
        (cx - 85, cy - 10),
    ], fill=(255, 255, 255, 235))
    return img

def icon_15_helix(base):
    """Double helix spiral suggesting upward DNA-like motion"""
    img = base.copy()
    cx, cy = SIZE // 2, SIZE // 2

    for strand in [0, 1]:
        phase = strand * math.pi
        layer = draw_on_layer(SIZE, lambda d: None)
        ld = ImageDraw.Draw(layer)
        for i in range(200):
            t = i / 199
            y = cy + 140 - t * 300
            x = cx + math.sin(t * math.pi * 3 + phase) * 60
            size = 6 + math.sin(t * math.pi * 3 + phase + math.pi/2) * 3
            alpha = int(100 + t * 140)
            ld.ellipse([x-size, y-size, x+size, y+size], fill=(255, 255, 255, alpha))
        img = Image.alpha_composite(img, layer)

    # Arrow tip at top
    d = ImageDraw.Draw(img)
    d.polygon([
        (cx, cy - 170),
        (cx + 40, cy - 130),
        (cx - 40, cy - 130),
    ], fill=(255, 255, 255, 230))
    return img


# ============ PALETTE DEFINITIONS ============
palettes = {
    "deep_blue":       ((20, 30, 65),   (55, 120, 210)),
    "midnight_indigo": ((15, 10, 45),   (60, 50, 180)),
    "ocean_depth":     ((8, 25, 50),    (20, 100, 160)),
    "dark_teal":       ((8, 30, 40),    (15, 140, 140)),
    "emerald_dark":    ((8, 30, 20),    (30, 150, 80)),
    "purple_dusk":     ((35, 15, 55),   (100, 60, 170)),
    "warm_charcoal":   ((30, 28, 25),   (80, 70, 60)),
    "steel_slate":     ((25, 30, 45),   (80, 110, 160)),
    "crimson_dark":    ((45, 12, 15),   (180, 40, 50)),
    "sunset_amber":    ((50, 25, 10),   (220, 120, 30)),
}

# Generate all combinations
count = 0
design_fns = [
    ("layered_arrows",  icon_01_layered_arrows),
    ("ribbon_arrow",    icon_02_ribbon_arrow),
    ("faceted_arrow",   icon_03_faceted_arrow),
    ("orbital_arrow",   icon_04_orbital_arrow),
    ("rising_particles",icon_05_rising_particles),
    ("neon_outline",    icon_06_neon_outline),
    ("speed_lines",     icon_07_speed_lines),
    ("gradient_arrow",  icon_08_gradient_arrow),
    ("concentric",      icon_10_concentric_rings),
    ("shield_upload",   icon_11_shield_upload),
    ("wave_arrow",      icon_12_wave_arrow),
    ("abstract_a",      icon_13_abstract_a),
    ("helix",           icon_15_helix),
]

combos = []
pal_list = list(palettes.items())

# Each design in ~4 palettes = ~52 icons
for di, (dname, dfn) in enumerate(design_fns):
    for pi in range(min(4, len(pal_list))):
        # Rotate palette selection per design
        pal_idx = (di * 3 + pi) % len(pal_list)
        combos.append((dname, dfn, pal_list[pal_idx]))
        if len(combos) >= 50:
            break
    if len(combos) >= 50:
        break

for idx, (dname, dfn, (pname, (top, bot))) in enumerate(combos[:50]):
    base = make_base(SIZE, PAD, 90, top, bot)
    try:
        result = dfn(base)
    except Exception as e:
        print(f"  Error on {dname}_{pname}: {e}")
        result = base
    fname = f"{idx+1:02d}_{dname}_{pname}.png"
    result.save(os.path.join(OUTPUT_DIR, fname), "PNG")
    print(f"  [{idx+1}/50] {fname}")

print(f"\nGenerated {min(len(combos), 50)} creative icons in {OUTPUT_DIR}")
