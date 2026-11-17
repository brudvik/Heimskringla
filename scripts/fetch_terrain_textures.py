"""Downloads CC0 terrain textures from ambientCG and packs them into two Texture2DArray images for the terrain shader.

Usage:
    python scripts/fetch_terrain_textures.py [zip-dir] [extracted-dir]

Default: zip-dir = D:/3dmodels/zip/ambientcg, extracted-dir = D:/3dmodels/extracted/ambientcg

Steps:
  1. Downloads <id>_2K-JPG.zip for each layer in LAYERS (skipped if the zip exists).
  2. Extracts to <extracted>/<id>/ (image files only, file names only - no paths from the zip are used).
  3. Scales to SIZE and stacks the layers on top of each other (1 layer = 1 slice). The terrain layers and
     the detail layers (bark/moss for trees and rocks, prefix "detail_") are packed separately:
       assets/terrain/terrain_albedo.png  RGB = color (sRGB), A = height (for height blending)
       assets/terrain/terrain_nrao.png    R,G = normal x,y (OpenGL), B = roughness, A = AO
  4. Writes .import files so Godot imports them as compressed Texture2DArrays with mipmaps,
     and assets/terrain/terrain_layers.json with the mean color of each layer (linear), which the shader
     uses to tint the textures towards the zone colors of the terrain.

The order of LAYERS defines the layer indices in the shader (shaders/terrain.gdshader) and in
Biomes.GroundWeights - if it changes, those must change too.
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

# Detail textures for props (bark and moss on trees/rocks): assets/terrain/detail_albedo.png / detail_nrao.png
DETAIL_LAYERS = [
    ("Bark014", "bark (spruce/pine)"),
    ("Bark012", "bark (deciduous)"),
    ("Moss002", "moss"),
]

# (ambientCG id, name) - order = layer index
LAYERS = [
    ("Grass004", "meadow"),
    ("Ground037", "deciduous forest floor"),
    ("Ground071", "conifer forest floor"),
    ("Ground068", "heath/bog/soil"),
    ("Rock030", "rock"),
    ("Gravel041", "scree/gravel"),
    ("Ground080", "sand"),
    ("Snow010A", "snow"),
]

UA = {"User-Agent": "heimskringla-asset-script"}
MAPS = {"Color": None, "NormalGL": None, "Roughness": None, "AmbientOcclusion": None, "Displacement": None}


def download(asset_id, zip_dir):
    path = os.path.join(zip_dir, f"{asset_id}_2K-JPG.zip")
    if os.path.exists(path) and os.path.getsize(path) > 0:
        return path
    url = f"https://ambientcg.com/get?file={asset_id}_2K-JPG.zip"
    print(f"  downloading {url}")
    req = urllib.request.Request(url, headers=UA)
    with urllib.request.urlopen(req) as r:
        data = r.read()
    zipfile.ZipFile(io.BytesIO(data))  # check that it is a valid zip before saving
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

        # Stretch the height to the full 0..255 (so the layers compete equally in the height blend)
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
            f.write("Textures from https://ambientcg.com, license CC0 1.0 (no attribution required):\n")
        for asset_id, name in LAYERS + DETAIL_LAYERS:
            if asset_id not in text:
                f.write(f"- {asset_id} ({name}) - https://ambientcg.com/view?id={asset_id}\n")
    print("Done:", OUT)


if __name__ == "__main__":
    main()
