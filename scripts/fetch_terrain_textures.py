"""Laster ned CC0-terrengteksturer fra ambientCG og pakker dem til to Texture2DArray-bilder for terrengshaderen.

Bruk:
    python scripts/fetch_terrain_textures.py [zip-mappe] [utpakket-mappe]

Standard: zip-mappe = D:/3dmodels/zip/ambientcg, utpakket-mappe = D:/3dmodels/extracted/ambientcg

Steg:
  1. Laster ned <id>_2K-JPG.zip for hvert lag i LAYERS (hoppes over hvis zip-en finnes).
  2. Pakker ut til <utpakket>/<id>/ (bare bildefiler, bare filnavn - ingen stier fra zip-en brukes).
  3. Skalerer til SIZE og pakker lagene oppå hverandre (1 lag = 1 skive). Terrenglagene og
     detaljlagene (bark/mose til trær og steiner, prefiks "detail_") pakkes hver for seg:
       assets/terrain/terrain_albedo.png  RGB = farge (sRGB), A = høyde (til høydeblanding)
       assets/terrain/terrain_nrao.png    R,G = normal x,y (OpenGL), B = ruhet, A = AO
  4. Skriver .import-filer så Godot importerer dem som komprimerte Texture2DArray med mipmaps,
     og assets/terrain/terrain_layers.json med snittfargen til hvert lag (lineær), som shaderen
     bruker til å tone teksturene mot sonefargene i terrenget.

Rekkefølgen i LAYERS er lag-indeksene i shaderen (shaders/terrain.gdshader) og i
Biomes.GroundWeights - endres den, må de endres også.
"""
import io
import json
import os
import sys
import urllib.request
import zipfile

from PIL import Image

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "assets", "terrain")
SIZE = 1024

# Detaljteksturer for props (bark og mose på trær/steiner): assets/terrain/detail_albedo.png / detail_nrao.png
DETAIL_LAYERS = [
    ("Bark014", "bark (gran/furu)"),
    ("Bark012", "bark (løvtre)"),
    ("Moss002", "mose"),
]

# (ambientCG-id, navn) - rekkefølgen = lagindeks
LAYERS = [
    ("Grass004", "eng"),
    ("Ground037", "lauvskogbunn"),
    ("Ground071", "barskogbunn"),
    ("Ground068", "lynghei/myr/jord"),
    ("Rock030", "berg"),
    ("Gravel041", "ur/grus"),
    ("Ground080", "sand"),
    ("Snow010A", "snø"),
]

UA = {"User-Agent": "heimskringla-asset-script"}
MAPS = {"Color": None, "NormalGL": None, "Roughness": None, "AmbientOcclusion": None, "Displacement": None}


def download(asset_id, zip_dir):
    path = os.path.join(zip_dir, f"{asset_id}_2K-JPG.zip")
    if os.path.exists(path) and os.path.getsize(path) > 0:
        return path
    url = f"https://ambientcg.com/get?file={asset_id}_2K-JPG.zip"
    print(f"  laster ned {url}")
    req = urllib.request.Request(url, headers=UA)
    with urllib.request.urlopen(req) as r:
        data = r.read()
    zipfile.ZipFile(io.BytesIO(data))  # sjekk at det er en gyldig zip før vi lagrer
    with open(path, "wb") as f:
        f.write(data)
    return path


def extract(zip_path, dest):
    os.makedirs(dest, exist_ok=True)
    with zipfile.ZipFile(zip_path) as z:
        for info in z.infolist():
            name = os.path.basename(info.filename)
            if not name.lower().endswith((".jpg", ".jpeg", ".png")):
                continue
            target = os.path.join(dest, name)
            if not os.path.exists(target):
                with z.open(info) as src, open(target, "wb") as dst:
                    dst.write(src.read())


def find_map(folder, key):
    for f in os.listdir(folder):
        stem = os.path.splitext(f)[0]
        if stem.endswith("_" + key):
            return os.path.join(folder, f)
    return None


def load(path, mode, fallback):
    if path is None:
        return Image.new(mode, (SIZE, SIZE), fallback)
    im = Image.open(path).convert(mode)
    return im.resize((SIZE, SIZE), Image.LANCZOS)


def srgb_to_linear(c):
    c /= 255.0
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4


def build(layers, prefix, zip_dir, ext_dir):
    albedo = Image.new("RGBA", (SIZE, SIZE * len(layers)))
    nrao = Image.new("RGBA", (SIZE, SIZE * len(layers)))
    means = []
    for i, (asset_id, name) in enumerate(layers):
        print(f"[{prefix} {i}] {asset_id} ({name})")
        folder = os.path.join(ext_dir, asset_id)
        extract(download(asset_id, zip_dir), folder)

        color = load(find_map(folder, "Color"), "RGB", (128, 128, 128))
        normal = load(find_map(folder, "NormalGL"), "RGB", (128, 128, 255))
        rough = load(find_map(folder, "Roughness"), "L", 200)
        ao = load(find_map(folder, "AmbientOcclusion"), "L", 255)
        disp = load(find_map(folder, "Displacement"), "L", 128)

        # Strekk høyden til hele 0..255 (lagene skal kunne konkurrere likt i høydeblandingen)
        lo, hi = disp.getextrema()
        if hi > lo:
            disp = disp.point(lambda v, lo=lo, hi=hi: int((v - lo) * 255 / (hi - lo)))

        r, g, b = color.split()
        albedo.paste(Image.merge("RGBA", (r, g, b, disp)), (0, i * SIZE))
        nr, ng, _ = normal.split()
        nrao.paste(Image.merge("RGBA", (nr, ng, rough, ao)), (0, i * SIZE))

        small = color.resize((64, 64), Image.BOX)
        px = list(small.getdata())  # noqa (Pillow < 14)
        mean = [sum(srgb_to_linear(float(p[c])) for p in px) / len(px) for c in range(3)]
        means.append({"id": asset_id, "name": name, "mean_linear": [round(m, 4) for m in mean]})

    albedo.save(os.path.join(OUT, f"{prefix}_albedo.png"), optimize=True)
    nrao.save(os.path.join(OUT, f"{prefix}_nrao.png"), optimize=True)
    with open(os.path.join(OUT, f"{prefix}_layers.json"), "w", encoding="utf-8") as f:
        json.dump(means, f, indent=2, ensure_ascii=False)

    for name in (f"{prefix}_albedo.png", f"{prefix}_nrao.png"):
        imp = os.path.join(OUT, name + ".import")
        with open(imp, "w", encoding="utf-8", newline="\n") as f:
            f.write(
                '[remap]\n\nimporter="2d_array_texture"\ntype="CompressedTexture2DArray"\n\n'
                "[params]\n\ncompress/mode=2\ncompress/high_quality=true\ncompress/lossy_quality=0.7\n"
                "compress/hdr_compression=1\ncompress/channel_pack=0\nmipmaps/generate=true\nmipmaps/limit=-1\n"
                f"slices/horizontal=1\nslices/vertical={len(layers)}\n"
            )


def main():
    zip_dir = sys.argv[1] if len(sys.argv) > 1 else "D:/3dmodels/zip/ambientcg"
    ext_dir = sys.argv[2] if len(sys.argv) > 2 else "D:/3dmodels/extracted/ambientcg"
    os.makedirs(zip_dir, exist_ok=True)
    os.makedirs(OUT, exist_ok=True)
    build(LAYERS, "terrain", zip_dir, ext_dir)
    build(DETAIL_LAYERS, "detail", zip_dir, ext_dir)

    lic = os.path.join(ROOT, "assets", "LICENSES.md")
    with open(lic, encoding="utf-8") as f:
        text = f.read()
    with open(lic, "a", encoding="utf-8", newline="\n") as f:
        if "ambientCG" not in text:
            f.write("\n## assets/terrain/ - ambientCG (CC0 1.0)\n\n")
            f.write("Teksturer fra https://ambientcg.com, lisens CC0 1.0 (ingen kreditering påkrevd):\n")
        for asset_id, name in LAYERS + DETAIL_LAYERS:
            if asset_id not in text:
                f.write(f"- {asset_id} ({name}) - https://ambientcg.com/view?id={asset_id}\n")
    print("Ferdig:", OUT)


if __name__ == "__main__":
    main()
