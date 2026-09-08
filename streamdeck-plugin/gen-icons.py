"""One-off icon generator for the NetMon Stream Deck plugin. Draws a simple
ascending signal-bars glyph (matching the app's ping/connection theme) at the
sizes the Stream Deck manifest requires. Not part of the build; run manually
whenever the artwork needs to change:

    python3 gen-icons.py
"""
from PIL import Image, ImageDraw

ROOT = "com.danielwinter.netmon.sdPlugin/imgs"
ACCENT = (0x4F, 0xA8, 0xE8, 255)  # matches NetMonGui's blue accent
DARK = (0x1A, 0x1A, 0x1F, 255)


def bars(draw, size, color, bar_count=4, pad_ratio=0.16):
    pad = size * pad_ratio
    usable = size - 2 * pad
    gap = usable * 0.14
    bar_w = (usable - gap * (bar_count - 1)) / bar_count
    for i in range(bar_count):
        h = usable * ((i + 1) / bar_count)
        x0 = pad + i * (bar_w + gap)
        x1 = x0 + bar_w
        y1 = size - pad
        y0 = y1 - h
        radius = min(bar_w, h) * 0.25
        draw.rounded_rectangle([x0, y0, x1, y1], radius=radius, fill=color)


def save_pair(name, draw_fn, size1, size2):
    for suffix, size in (("", size1), ("@2x", size2)):
        img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
        d = ImageDraw.Draw(img)
        draw_fn(d, size)
        img.save(f"{ROOT}/{name}{suffix}.png")


# Category icon: monochrome white bars on transparent, 28/56.
save_pair("plugin/category-icon", lambda d, s: bars(d, s, (255, 255, 255, 255)), 28, 56)

# Action list icon: same glyph, 20/40.
save_pair("actions/connection-stat/icon", lambda d, s: bars(d, s, (255, 255, 255, 255)), 20, 40)

# Default key artwork (overridden at runtime by the live SVG tile, this is just
# the placeholder shown before the plugin's first successful poll): dark tile
# with the accent-colored bars glyph, 72/144.
def draw_key(d, s):
    d.rounded_rectangle([0, 0, s, s], radius=s * 0.16, fill=DARK)
    bars(d, s, ACCENT, pad_ratio=0.22)

save_pair("actions/connection-stat/key", draw_key, 72, 144)

# Marketplace / plugin list icon: accent circle with bars, 256 only (per spec
# a single high-res image is used and auto-scaled — no @2x variant needed).
img = Image.new("RGBA", (256, 256), (0, 0, 0, 0))
d = ImageDraw.Draw(img)
d.ellipse([8, 8, 248, 248], fill=DARK)
bars(d, 256, ACCENT, pad_ratio=0.30)
img.save(f"{ROOT}/plugin/marketplace.png")

print("icons written")
