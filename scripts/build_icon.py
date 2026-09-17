"""Build the Windows ICO and title-bar PNG from the approved transparent master."""

from pathlib import Path
import sys

from PIL import Image


def fit_on_square(source: Image.Image, size: int, inset: float = 0.04) -> Image.Image:
    canvas = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    usable = max(1, round(size * (1 - inset * 2)))
    scale = min(usable / source.width, usable / source.height)
    dimensions = (max(1, round(source.width * scale)), max(1, round(source.height * scale)))
    resized = source.resize(dimensions, Image.Resampling.LANCZOS)
    canvas.alpha_composite(resized, ((size - dimensions[0]) // 2, (size - dimensions[1]) // 2))
    return canvas


if len(sys.argv) != 4:
    raise SystemExit("usage: build_icon.py MASTER.png OUTPUT.ico TITLEBAR.png")

master_path, ico_path, titlebar_path = map(Path, sys.argv[1:])
master = Image.open(master_path).convert("RGBA")
if any(master.getpixel(point)[3] != 0 for point in ((0, 0), (master.width - 1, 0), (0, master.height - 1), (master.width - 1, master.height - 1))):
    raise SystemExit("master image does not have transparent corners")

ico_path.parent.mkdir(parents=True, exist_ok=True)
frames = [fit_on_square(master, size) for size in (16, 20, 24, 32, 40, 48, 64, 128, 256)]
frames[-1].save(
    ico_path,
    format="ICO",
    append_images=frames[:-1],
    sizes=[(size, size) for size in (16, 20, 24, 32, 40, 48, 64, 128, 256)],
    bitmap_format="png",
)
fit_on_square(master, 64, inset=0.02).save(titlebar_path, format="PNG", optimize=True)
print(f"Created {ico_path}")
print(f"Created {titlebar_path}")
