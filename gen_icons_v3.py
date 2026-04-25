from PIL import Image, ImageDraw, ImageFilter
import math, os

OUTPUT_DIR = r"E:\Projects\CSUploader\CSUploader\src\Properties\Images\Logo\candidates_v3"
os.makedirs(OUTPUT_DIR, exist_ok=True)

SIZE = 512
CX, CY = SIZE // 2, SIZE // 2


def draw_globe(draw, cx, cy, r, base_color, highlight_color, shadow_color):
    """Draw a stylized globe with latitude/longitude lines"""
    # Globe body - gradient sphere effect via concentric circles
    for i in range(r, 0, -1):
        t = 1.0 - (i / r)
        # Shift highlight to upper-left
        cr = int(base_color[0] + t * (highlight_color[0] - base_color[0]) * 0.6)
        cg = int(base_color[1] + t * (highlight_color[1] - base_color[1]) * 0.6)
        cb = int(base_color[2] + t * (highlight_color[2] - base_color[2]) * 0.6)
        draw.ellipse([cx-i, cy-i, cx+i, cy+i], fill=(cr, cg, cb, 255))

    # Specular highlight (upper left)
    highlight = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    hd = ImageDraw.Draw(highlight)
    hr = int(r * 0.35)
    hx, hy = cx - int(r * 0.3), cy - int(r * 0.3)
    for i in range(hr, 0, -1):
        alpha = int(80 * (1 - i / hr))
        hd.ellipse([hx-i, hy-i, hx+i, hy+i], fill=(255, 255, 255, alpha))
    highlight = highlight.filter(ImageFilter.GaussianBlur(8))
    return highlight

def draw_globe_lines(draw, cx, cy, r, line_color):
    """Draw latitude and longitude grid lines on globe"""
    # Latitude lines
    for lat in [-0.5, -0.2, 0.15, 0.45]:
        y = cy + int(lat * r)
        # Width of latitude line at this height
        h = abs(lat)
        w = int(math.sqrt(max(0, 1 - lat*lat)) * r)
        if w > 10:
            draw.arc([cx-w, y-8, cx+w, y+8], 0, 180, fill=line_color, width=2)

    # Longitude lines (elliptical arcs)
    for lon_offset in [-0.4, 0.0, 0.4]:
        w = int(abs(lon_offset) * r) if lon_offset != 0 else int(r * 0.05)
        w = max(w, 3)
        draw.arc([cx-w, cy-r+5, cx+w, cy+r-5], 0, 360, fill=line_color, width=2)

    # Equator
    draw.arc([cx-r+5, cy-int(r*0.08), cx+r-5, cy+int(r*0.08)], 0, 360, fill=line_color, width=2)


def draw_curved_arrow_up(img, cx, cy, globe_r, arrow_color, arrow_highlight,
                          start_angle=200, end_angle=-30, thickness=28, head_size=45):
    """Draw a curved arrow sweeping upward around the globe"""
    layer = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)

    path_r = globe_r + thickness // 2 + 8

    # Generate arrow path points
    points = []
    a_start = math.radians(start_angle)
    a_end = math.radians(end_angle)
    steps = 200
    for i in range(steps + 1):
        t = i / steps
        angle = a_start + t * (a_end - a_start)
        # Gradually increase radius for spiral effect
        spiral_r = path_r + t * 15
        x = cx + spiral_r * math.cos(angle)
        y = cy + spiral_r * math.sin(angle)
        points.append((x, y, angle))

    # Draw arrow body as thick line segments
    for i in range(len(points) - 1):
        x1, y1, a1 = points[i]
        x2, y2, a2 = points[i + 1]
        # Taper: thicker at start, thinner toward tip
        t = i / len(points)
        w = int(thickness * (1 - t * 0.3))

        # Darker outer edge, lighter inner
        d.line([(x1, y1), (x2, y2)], fill=arrow_color, width=w)

    # Arrowhead at the end
    ex, ey, ea = points[-1]
    # Direction of arrow at tip
    px, py, _ = points[-5]
    dx, dy = ex - px, ey - py
    length = math.sqrt(dx*dx + dy*dy)
    if length > 0:
        dx, dy = dx / length, dy / length

    # Perpendicular
    nx, ny = -dy, dx

    tip_x = ex + dx * head_size
    tip_y = ey + dy * head_size
    left_x = ex - dx * 5 + nx * head_size * 0.7
    left_y = ey - dy * 5 + ny * head_size * 0.7
    right_x = ex - dx * 5 - nx * head_size * 0.7
    right_y = ey - dy * 5 - ny * head_size * 0.7

    d.polygon([(tip_x, tip_y), (left_x, left_y), (right_x, right_y)], fill=arrow_color)

    # Add highlight stripe along the arrow
    highlight = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    hd = ImageDraw.Draw(highlight)
    for i in range(len(points) - 1):
        x1, y1, a1 = points[i]
        x2, y2, a2 = points[i + 1]
        t = i / len(points)
        w = max(2, int(thickness * 0.3 * (1 - t * 0.3)))
        # Offset inward slightly
        inner_offset = 3
        ix1 = x1 + inner_offset * math.cos(a1 + math.pi/2)
        iy1 = y1 + inner_offset * math.sin(a1 + math.pi/2)
        ix2 = x2 + inner_offset * math.cos(a2 + math.pi/2)
        iy2 = y2 + inner_offset * math.sin(a2 + math.pi/2)
        hd.line([(ix1, iy1), (ix2, iy2)], fill=arrow_highlight, width=w)

    highlight = highlight.filter(ImageFilter.GaussianBlur(2))
    layer = Image.alpha_composite(layer, highlight)

    return Image.alpha_composite(img, layer)


def draw_shadow(img, offset_x=4, offset_y=6, blur=12):
    """Add drop shadow beneath the content"""
    # Extract alpha channel as shadow base
    alpha = img.split()[3]
    shadow = Image.new("RGBA", img.size, (0, 0, 0, 0))
    shadow_layer = Image.new("RGBA", img.size, (0, 0, 0, 60))
    shadow_layer.putalpha(alpha)

    # Offset
    offset_shadow = Image.new("RGBA", img.size, (0, 0, 0, 0))
    offset_shadow.paste(shadow_layer, (offset_x, offset_y))
    offset_shadow = offset_shadow.filter(ImageFilter.GaussianBlur(blur))

    result = Image.alpha_composite(shadow, offset_shadow)
    return Image.alpha_composite(result, img)


# ============ DESIGN VARIANTS ============

def make_globe_icon(globe_base, globe_highlight, globe_shadow, globe_line,
                     arrow_color, arrow_highlight, arrow_start, arrow_end,
                     globe_r=130, bg_color=None):
    """Generate a globe-with-arrow icon"""
    if bg_color:
        img = Image.new("RGBA", (SIZE, SIZE), bg_color)
    else:
        img = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))

    d = ImageDraw.Draw(img)

    # Globe shadow
    shadow = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    sd = ImageDraw.Draw(shadow)
    sd.ellipse([CX-globe_r+5, CY-globe_r+8, CX+globe_r+5, CY+globe_r+8],
               fill=(0, 0, 0, 40))
    shadow = shadow.filter(ImageFilter.GaussianBlur(10))
    img = Image.alpha_composite(img, shadow)

    # Globe body
    highlight_layer = draw_globe(d, CX, CY, globe_r, globe_base, globe_highlight, globe_shadow)
    img = Image.alpha_composite(img, highlight_layer)

    # Globe grid lines
    draw_globe_lines(d, CX, CY, globe_r, globe_line)

    # Curved arrow
    img = draw_curved_arrow_up(img, CX, CY, globe_r, arrow_color, arrow_highlight,
                                start_angle=arrow_start, end_angle=arrow_end)

    return img


# Color schemes
schemes = [
    {
        "name": "blue_gold",
        "globe_base": (30, 80, 160), "globe_highlight": (80, 160, 220),
        "globe_shadow": (15, 40, 80), "globe_line": (60, 130, 200, 120),
        "arrow": (240, 190, 40, 255), "arrow_hi": (255, 230, 120, 100),
    },
    {
        "name": "green_orange",
        "globe_base": (25, 100, 60), "globe_highlight": (60, 180, 100),
        "globe_shadow": (10, 50, 30), "globe_line": (50, 150, 90, 120),
        "arrow": (230, 150, 30, 255), "arrow_hi": (255, 210, 100, 100),
    },
    {
        "name": "teal_white",
        "globe_base": (20, 90, 110), "globe_highlight": (60, 180, 200),
        "globe_shadow": (10, 45, 55), "globe_line": (50, 150, 170, 120),
        "arrow": (240, 245, 250, 255), "arrow_hi": (255, 255, 255, 120),
    },
    {
        "name": "dark_blue_cyan",
        "globe_base": (20, 50, 120), "globe_highlight": (50, 120, 200),
        "globe_shadow": (10, 25, 60), "globe_line": (40, 100, 180, 120),
        "arrow": (40, 220, 220, 255), "arrow_hi": (140, 255, 255, 100),
    },
    {
        "name": "purple_gold",
        "globe_base": (60, 30, 120), "globe_highlight": (120, 70, 200),
        "globe_shadow": (30, 15, 60), "globe_line": (100, 60, 180, 120),
        "arrow": (240, 200, 50, 255), "arrow_hi": (255, 240, 140, 100),
    },
    {
        "name": "slate_green",
        "globe_base": (50, 60, 80), "globe_highlight": (100, 130, 160),
        "globe_shadow": (25, 30, 40), "globe_line": (80, 110, 140, 120),
        "arrow": (80, 220, 120, 255), "arrow_hi": (160, 255, 180, 100),
    },
    {
        "name": "ocean_red",
        "globe_base": (20, 60, 100), "globe_highlight": (50, 130, 180),
        "globe_shadow": (10, 30, 50), "globe_line": (40, 110, 160, 120),
        "arrow": (220, 60, 50, 255), "arrow_hi": (255, 140, 130, 100),
    },
    {
        "name": "earth_classic",
        "globe_base": (30, 90, 50), "globe_highlight": (70, 160, 90),
        "globe_shadow": (15, 45, 25), "globe_line": (50, 130, 70, 100),
        "arrow": (220, 180, 40, 255), "arrow_hi": (255, 230, 120, 100),
    },
]

# Arrow sweep variants
arrow_sweeps = [
    ("wrap_right_up",  200, -30),   # Wraps from lower-left up to upper-right
    ("wrap_left_up",   340, 170),   # Wraps from lower-right up to upper-left
    ("half_orbit",     230, 10),    # Half orbit ending at top
    ("tight_sweep",    210, 30),    # Tighter sweep
    ("wide_sweep",     240, -60),   # Wider dramatic sweep
    ("three_quarter",  250, -20),   # Three-quarter wrap
]

# Background options
backgrounds = [
    ("transparent", None),
    ("dark",        (25, 30, 40, 255)),
    ("white",       (245, 248, 250, 255)),
]

count = 0
for si, scheme in enumerate(schemes):
    for ai, (aname, a_start, a_end) in enumerate(arrow_sweeps):
        if count >= 50:
            break
        # Cycle through backgrounds
        bg_name, bg_color = backgrounds[count % len(backgrounds)]

        icon = make_globe_icon(
            globe_base=scheme["globe_base"],
            globe_highlight=scheme["globe_highlight"],
            globe_shadow=scheme["globe_shadow"],
            globe_line=scheme["globe_line"],
            arrow_color=scheme["arrow"],
            arrow_highlight=scheme["arrow_hi"],
            arrow_start=a_start,
            arrow_end=a_end,
            bg_color=bg_color,
        )

        fname = f"{count+1:02d}_{scheme['name']}_{aname}_{bg_name}.png"
        icon.save(os.path.join(OUTPUT_DIR, fname), "PNG")
        print(f"  [{count+1}/50] {fname}")
        count += 1
    if count >= 50:
        break

print(f"\nGenerated {count} globe icons in {OUTPUT_DIR}")
