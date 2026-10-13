"""Convert the supplied Otter Monitor logo to a Windows ICO. Requires Pillow."""
from pathlib import Path
from PIL import Image, ImageOps

ROOT = Path(__file__).resolve().parents[1]
with Image.open(ROOT / "assets/otter-logo.png") as source:
    # Preserve the original artwork and its background; only fit it to ICO sizes.
    image = ImageOps.pad(source.convert("RGBA"), (256, 256),
                         method=Image.Resampling.LANCZOS, color=source.getpixel((0, 0)))
    image.save(ROOT / "src/PortPilot.App/otter-monitor.ico",
               sizes=[(s, s) for s in (16, 20, 24, 32, 40, 48, 64, 128, 256)])
print("Generated nine-resolution Otter Monitor Windows ICO.")
