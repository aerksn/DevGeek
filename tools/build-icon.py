"""Rebuild DevGeek's original vector-style icon. Requires Python and Pillow."""
from pathlib import Path
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[1]
SCALE = 4
SIZE = 256
image = Image.new("RGBA", (SIZE * SCALE, SIZE * SCALE))
mask = Image.new("L", image.size)
ImageDraw.Draw(mask).rounded_rectangle((8*SCALE, 8*SCALE, 248*SCALE, 248*SCALE), radius=56*SCALE, fill=255)
pixels = image.load()
for y in range(image.height):
    for x in range(image.width):
        t = (x + y) / (2 * (image.width - 1))
        pixels[x, y] = tuple(round(a + (b-a)*t) for a, b in zip((84, 64, 126), (38, 41, 61))) + (255,)
image.putalpha(mask)
draw = ImageDraw.Draw(image)
blue, rose, white = "#CFDDFB", "#F3C2D7", "#F5F2FC"
def line(points, color, width=10):
    coords = [(x*SCALE, y*SCALE) for x, y in points]
    draw.line(coords, fill=color, width=width*SCALE, joint="curve")
    for x, y in (coords[0], coords[-1]):
        r = width*SCALE/2
        draw.ellipse((x-r, y-r, x+r, y+r), fill=color)

draw.rounded_rectangle((42*SCALE, 76*SCALE, 112*SCALE, 130*SCALE), radius=18*SCALE, outline=blue, width=10*SCALE)
draw.rounded_rectangle((144*SCALE, 76*SCALE, 214*SCALE, 130*SCALE), radius=18*SCALE, outline=rose, width=10*SCALE)
line([(112, 94), (144, 94)], white)
line([(30, 82), (44, 88)], blue)
line([(212, 88), (226, 82)], rose)
line([(83, 163), (102, 181), (83, 199)], blue, 9)
line([(121, 199), (172, 199)], rose, 9)
out = ROOT / "assets"
out.mkdir(exist_ok=True)
image = image.resize((SIZE, SIZE), Image.Resampling.LANCZOS)
image.save(out / "devgeek.png")
image.save(ROOT / "src/PortPilot.App/devgeek.ico", sizes=[(s,s) for s in (16, 20, 24, 32, 40, 48, 64, 128, 256)])
print("Generated DevGeek PNG and nine-resolution Windows ICO.")
