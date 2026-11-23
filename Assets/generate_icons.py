"""Generate platform icons from icon.svg.

Requires Pillow. Run from the repository root with: python Assets/generate_icons.py
"""

from pathlib import Path
import re
import xml.etree.ElementTree as ET

from PIL import Image, ImageDraw


ROOT = Path(__file__).resolve().parent
SIZE = 1024
SCALE = 4
SIZES = (16, 32, 48, 64, 128, 256, 512, 1024)


def create_master():
    root = ET.parse(ROOT / "icon.svg").getroot()
    if root.attrib.get("viewBox") != "0 0 1024 1024":
        raise ValueError("The icon must have a 1024 x 1024 viewBox")
    rect = root.find("{http://www.w3.org/2000/svg}rect")
    path = root.find("{http://www.w3.org/2000/svg}path")
    if rect is None or path is None:
        raise ValueError("The icon must contain a rounded rectangle and a path")

    x, y = (int(rect.attrib[key]) for key in ("x", "y"))
    width, height = (int(rect.attrib[key]) for key in ("width", "height"))
    path_data = path.attrib["d"]
    if re.sub(r"[MLZ\d\s,-]", "", path_data):
        raise ValueError("The icon path only supports absolute M/L/Z commands")
    numbers = [int(value) for value in re.findall(r"-?\d+", path_data)]
    if len(numbers) < 6 or len(numbers) % 2:
        raise ValueError("The icon path must contain coordinate pairs")
    points = list(zip(numbers[::2], numbers[1::2]))

    image = Image.new("RGBA", (SIZE * SCALE, SIZE * SCALE))
    draw = ImageDraw.Draw(image)
    draw.rounded_rectangle(
        tuple(value * SCALE for value in (x, y, x + width, y + height)),
        radius=int(rect.attrib["rx"]) * SCALE,
        fill=rect.attrib["fill"],
    )
    draw.polygon(
        [(x * SCALE, y * SCALE) for x, y in points],
        fill=path.attrib["fill"],
    )
    return image.resize((SIZE, SIZE), Image.Resampling.LANCZOS)


def main():
    image = create_master()
    image.save(ROOT / "icon.png", optimize=True)
    image.save(ROOT / "icon.ico", sizes=[(size, size) for size in SIZES if size <= 256])
    image.save(ROOT / "icon.icns", sizes=[(size, size) for size in SIZES if size >= 128])


if __name__ == "__main__":
    main()
