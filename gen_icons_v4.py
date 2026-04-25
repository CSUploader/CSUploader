from PIL import Image, ImageDraw, ImageFilter
import math, os

OUTPUT_DIR = r"E:\Projects\CSUploader\CSUploader\src\Properties\Images\Logo\candidates_v4"
os.makedirs(OUTPUT_DIR, exist_ok=True)

SIZE = 512
CX, CY = SIZE // 2, SIZE // 2


def draw_sphere(size, cx, cy, r, base_rgb, light_rgb, dark_rgb):
    """Draw a shaded sphere with proper lighting from upper-left"""
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    light_x, light_y = cx - r * 0.35, cy - r * 0.35

    for y in range(cy - r, cy + r + 1):
        for x in range(cx - r, cx + r + 1):
            dx, dy = x - cx, y - cy
            dist_sq = dx * dx + dy * dy
            if dist_sq > r * r:
                continue

            dist = math.sqrt(dist_sq)
            # Normal-based shading
            nz = math.sqrt(max(0, 1 - (dist / r) ** 2))

            # Light direction from upper-left
            lx, ly = light_x - x, light_y - y
            ll = math.sqrt(lx * lx + ly * ly + r * r)
            ldot = max(0, (lx * 0 + ly * 0 + r * nz) / ll)

            # Diffuse + ambient
            ambient = 0.3
            diffuse = ldot * 0.6
            brightness = min(1.0, ambient + diffuse)

            # Edge darkening (fresnel-like)
            edge = 1.0 - nz
            edge_darken = edge * edge * 0.4

            # Mix base toward light or dark
            if brightness > 0.5:
                t = (brightness - 0.5) * 2
                cr = int(base_rgb[0] + t * (light_rgb[0] - base_rgb[0]) - edge_darken * 60)
                cg = int(base_rgb[1] + t * (light_rgb[1] - base_rgb[1]) - edge_darken * 60)
                cb = int(base_rgb[2] + t * (light_rgb[2] - base_rgb[2]) - edge_darken * 40)
            else:
                t = brightness * 2
                cr = int(dark_rgb[0] + t * (base_rgb[0] - dark_rgb[0]) - edge_darken * 40)
                cg = int(dark_rgb[1] + t * (base_rgb[1] - dark_rgb[1]) - edge_darken * 40)
                cb = int(dark_rgb[2] + t * (base_rgb[2] - dark_rgb[2]) - edge_darken * 30)

            cr, cg, cb = max(0, min(255, cr)), max(0, min(255, cg)), max(0, min(255, cb))

            # Anti-alias edge
            edge_dist = r - dist
            alpha = 255 if edge_dist > 1.5 else int(255 * edge_dist / 1.5)
            alpha = max(0, min(255, alpha))

            img.putpixel((x, y), (cr, cg, cb, alpha))

    return img


def draw_specular(size, cx, cy, r):
    """Small bright specular highlight"""
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    hx = cx - int(r * 0.28)
    hy = cy - int(r * 0.32)
    hr = int(r * 0.18)
    d = ImageDraw.Draw(img)
    for i in range(hr, 0, -1):
        t = 1 - i / hr
        alpha = int(90 * t * t)
        d.ellipse([hx - i, hy - i, hx + i, hy + i], fill=(255, 255, 255, alpha))
    return img.filter(ImageFilter.GaussianBlur(4))


def draw_grid_lines(img, cx, cy, r, color):
    """Draw latitude/longitude lines that respect the sphere curvature"""
    d = ImageDraw.Draw(img)

    # Longitude lines (vertical ellipses)
    for lon in [-0.5, -0.2, 0.15, 0.5]:
        w = int(abs(lon) * r * 0.9)
        if w < 5:
            w = 5
        x_offset = int(lon * r * 0.5)
        d.arc([cx + x_offset - w, cy - r + 10, cx + x_offset + w, cy + r - 10],
              0, 360, fill=color, width=2)

    # Latitude lines (horizontal arcs)
    for lat in [-0.55, -0.2, 0.2, 0.55]:
        y_pos = cy + int(lat * r)
        half_w = int(math.sqrt(max(0.01, 1 - lat * lat)) * r * 0.92)
        h = int(r * 0.06)
        if half_w > 15:
            d.arc([cx - half_w, y_pos - h, cx + half_w, y_pos + h],
                  0, 360, fill=color, width=2)

    return img


def draw_smooth_arrow(img, cx, cy, globe_r, color, highlight_color,
                       start_deg=210, end_deg=-40, thickness=26, head_len=50):
    """Draw a smooth, thick curved arrow sweeping around the globe"""
    layer = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)

    orbit_r = globe_r + thickness // 2 + 12
    a_start = math.radians(start_deg)
    a_end = math.radians(end_deg)
    steps = 400

    # Build the path
    path = []
    for i in range(steps + 1):
        t = i / steps
        angle = a_start + t * (a_end - a_start)
        spiral = orbit_r + t * 18
        x = cx + spiral * math.cos(angle)
        y = cy + spiral * math.sin(angle)
        path.append((x, y, angle, t))

    # Draw thick arrow body using filled circles along path (smooth)
    for i in range(len(path) - 15):
        x, y, angle, t = path[i]
        w = thickness * (0.6 + 0.4 * (1 - t))
        r_dot = w / 2
        d.ellipse([x - r_dot, y - r_dot, x + r_dot, y + r_dot], fill=color)

    # Highlight stripe (inner edge, thinner)
    hl = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    hd = ImageDraw.Draw(hl)
    for i in range(len(path) - 20):
        x, y, angle, t = path[i]
        w = thickness * 0.25 * (0.6 + 0.4 * (1 - t))
        # Offset toward center of globe
        off_x = (cx - x) * 0.12
        off_y = (cy - y) * 0.12
        hd.ellipse([x + off_x - w, y + off_y - w, x + off_x + w, y + off_y + w],
                    fill=highlight_color)
    hl = hl.filter(ImageFilter.GaussianBlur(3))
    layer = Image.alpha_composite(layer, hl)

    # Arrowhead
    d2 = ImageDraw.Draw(layer)
    end_idx = len(path) - 1
    ex, ey, ea, _ = path[end_idx]
    # Direction from a few steps back
    bx, by, _, _ = path[end_idx - 15]
    dx, dy = ex - bx, ey - by
    length = math.sqrt(dx * dx + dy * dy)
    if length > 0:
        dx, dy = dx / length, dy / length
    nx, ny = -dy, dx

    tip = (ex + dx * head_len, ey + dy * head_len)
    base_w = head_len * 0.65
    left = (ex - dx * 2 + nx * base_w, ey - dy * 2 + ny * base_w)
    right = (ex - dx * 2 - nx * base_w, ey - dy * 2 - ny * base_w)

    d2.polygon([tip, left, right], fill=color)

    # Arrowhead highlight
    mid = ((left[0] + right[0]) / 2 + tip[0]) / 2, ((left[1] + right[1]) / 2 + tip[1]) / 2
    hl_pts = [
        tip,
        ((tip[0] + left[0]) / 2, (tip[1] + left[1]) / 2),
        mid,
    ]
    # Small triangular highlight on the arrowhead
    d2.polygon(hl_pts, fill=highlight_color)

    return Image.alpha_composite(img, layer)


# ============ SCHEMES ============
schemes = [
    {
        "name": "blue_gold",
        "globe": ((35, 90, 170), (100, 180, 240), (15, 40, 80)),
        "grid": (70, 140, 210, 100),
        "arrow": (235, 185, 35, 255), "arrow_hi": (255, 235, 130, 120),
    },
    {
        "name": "green_gold",
        "globe": ((30, 110, 55), (70, 190, 100), (12, 50, 25)),
        "grid": (55, 160, 80, 100),
        "arrow": (225, 170, 30, 255), "arrow_hi": (255, 220, 110, 120),
    },
    {
        "name": "teal_orange",
        "globe": ((20, 100, 120), (60, 190, 210), (8, 45, 55)),
        "grid": (50, 160, 180, 100),
        "arrow": (230, 140, 30, 255), "arrow_hi": (255, 200, 100, 120),
    },
    {
        "name": "navy_cyan",
        "globe": ((20, 55, 130), (55, 130, 210), (8, 25, 60)),
        "grid": (45, 110, 190, 100),
        "arrow": (30, 210, 220, 255), "arrow_hi": (130, 245, 250, 120),
    },
    {
        "name": "purple_amber",
        "globe": ((65, 30, 130), (130, 75, 210), (30, 12, 60)),
        "grid": (110, 65, 190, 100),
        "arrow": (235, 190, 40, 255), "arrow_hi": (255, 230, 130, 120),
    },
    {
        "name": "dark_blue_white",
        "globe": ((25, 60, 140), (70, 140, 220), (10, 28, 65)),
        "grid": (55, 120, 200, 100),
        "arrow": (240, 242, 248, 255), "arrow_hi": (255, 255, 255, 140),
    },
    {
        "name": "earth_orange",
        "globe": ((30, 95, 55), (65, 175, 95), (14, 48, 28)),
        "grid": (50, 145, 75, 90),
        "arrow": (240, 150, 25, 255), "arrow_hi": (255, 210, 100, 120),
    },
    {
        "name": "steel_lime",
        "globe": ((50, 65, 85), (110, 140, 170), (22, 30, 40)),
        "grid": (85, 115, 150, 90),
        "arrow": (120, 220, 60, 255), "arrow_hi": (190, 255, 140, 120),
    },
]

sweeps = [
    ("sweep_up",      210, -40),
    ("sweep_wide",    230, -60),
    ("sweep_tight",   200, -20),
    ("half_wrap",     220,   0),
    ("three_quarter", 250, -30),
    ("low_sweep",     190, -10),
]

count = 0
results = []
for si, s in enumerate(schemes):
    for ai, (aname, a0, a1) in enumerate(sweeps):
        if count >= 50:
            break
        gb, gl, gd = s["globe"]
        sphere = draw_sphere(SIZE, CX, CY, 130, gb, gl, gd)
        spec = draw_specular(SIZE, CX, CY, 130)
        sphere = Image.alpha_composite(sphere, spec)
        sphere = draw_grid_lines(sphere, CX, CY, 130, s["grid"])

        # Add drop shadow
        shadow = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
        sd = ImageDraw.Draw(shadow)
        sd.ellipse([CX - 125, CY + 100, CX + 125, CY + 145], fill=(0, 0, 0, 50))
        shadow = shadow.filter(ImageFilter.GaussianBlur(15))

        canvas = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
        canvas = Image.alpha_composite(canvas, shadow)
        canvas = Image.alpha_composite(canvas, sphere)
        canvas = draw_smooth_arrow(canvas, CX, CY, 130,
                                    s["arrow"], s["arrow_hi"], a0, a1)

        fname = f"{count+1:02d}_{s['name']}_{aname}.png"
        canvas.save(os.path.join(OUTPUT_DIR, fname), "PNG")
        print(f"  [{count+1}/50] {fname}")
        count += 1
    if count >= 50:
        break

print(f"\nGenerated {count} icons in {OUTPUT_DIR}")
