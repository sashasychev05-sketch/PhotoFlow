"""Render the original PhotoFlow camera mark to Windows application icon sizes. Requires Pillow."""
from pathlib import Path
from PIL import Image, ImageDraw
folder = Path(__file__).resolve().parents[1] / "src" / "PhotoFlow.App" / "Assets"
folder.mkdir(parents=True, exist_ok=True)
image = Image.new("RGBA", (512, 512), (0, 0, 0, 0))
draw = ImageDraw.Draw(image)
draw.rounded_rectangle((12, 12, 500, 500), radius=122, fill="#d0f590")
draw.rounded_rectangle((106, 172, 406, 374), radius=42, outline="#24331c", width=24)
draw.line([(166, 172), (202, 120), (306, 120), (344, 172)], fill="#24331c", width=24, joint="curve")
draw.ellipse((196, 214, 318, 336), outline="#24331c", width=24)
image.save(folder / "PhotoFlow.png")
image.save(folder / "PhotoFlow.ico", sizes=[(16,16),(24,24),(32,32),(48,48),(64,64),(128,128),(256,256)])
