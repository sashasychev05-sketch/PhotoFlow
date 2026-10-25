"""Create original procedural landscape fixtures, with no personal photos or EXIF. Requires Pillow."""
from pathlib import Path
from PIL import Image, ImageDraw
import math
import random

destination = Path(__file__).resolve().parents[1] / "src" / "PhotoFlow.App" / "Assets" / "Demo"
destination.mkdir(parents=True, exist_ok=True)
palettes = [
    ((183, 206, 203), (232, 220, 185), (89, 126, 117), (34, 67, 61)),
    ((209, 168, 130), (247, 221, 172), (181, 116, 78), (105, 69, 50)),
    ((138, 166, 174), (216, 217, 196), (103, 127, 121), (40, 65, 67)),
    ((95, 131, 121), (187, 199, 158), (67, 99, 69), (27, 55, 42)),
]
names = ["Coast", "Dunes", "Alpine", "Forest"]
for index in range(12):
    random.seed(index)
    top, bottom, back, front = palettes[index % 4]
    width, height = 960, 680
    gradient = Image.new("RGB", (1, height))
    gradient.putdata([tuple(int(a + (b-a)*y/(height-1)) for a,b in zip(top,bottom)) for y in range(height)])
    image = gradient.resize((width, height))
    draw = ImageDraw.Draw(image)
    sun_x = 200 + (index % 3) * 230
    draw.ellipse((sun_x-48, 124, sun_x+48, 220), fill=(245, 231, 188))
    for layer, color in enumerate((back, front)):
        baseline = 345 + layer * 150
        points = [(0, height)] + [(x, int(baseline + 80*math.sin(x/230 + index + layer) + 32*math.sin(x/83 + index))) for x in range(0,width+1,8)] + [(width,height)]
        draw.polygon(points, fill=color)
    # Fine deterministic grain makes image decoding and thumbnails representative of raster assets.
    for _ in range(12000):
        x,y = random.randrange(width), random.randrange(height)
        original = image.getpixel((x,y)); delta = random.choice((-4,-2,2,4))
        draw.point((x,y), fill=tuple(max(0,min(255,c+delta)) for c in original))
    image.save(destination / f"{names[index % 4]}_{index+1:02}.jpg", quality=92, optimize=True)
print(f"Created 12 original demo illustrations in {destination}")
