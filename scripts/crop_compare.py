"""Crops the same region from several screenshots and places them side by side (to compare variants).

Usage: python scripts/crop_compare.py <out.png> <x0,y0,x1,y1> <image1> [image2 ...]
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
