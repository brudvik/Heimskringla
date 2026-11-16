"""Klipper ut samme område fra flere skjermbilder og legger dem side om side (for å sammenligne varianter).

Bruk: python scripts/crop_compare.py <ut.png> <x0,y0,x1,y1> <bilde1> [bilde2 ...]
"""
import sys
from PIL import Image, ImageDraw

out, box, files = sys.argv[1], tuple(int(v) for v in sys.argv[2].split(",")), sys.argv[3:]
crops = [Image.open(f).convert("RGB").crop(box) for f in files]
w, h = crops[0].size
sheet = Image.new("RGB", (w * len(crops), h + 24), "black")
d = ImageDraw.Draw(sheet)
for i, (c, f) in enumerate(zip(crops, files)):
    sheet.paste(c, (i * w, 24))
    d.text((i * w + 6, 6), f.replace("\\", "/").split("/")[-2], fill="white")
sheet.save(out)
print(out)
