"""Downloads CC0 models (glTF, 1K textures) from Poly Haven to assets/polyhaven/<id>/.

Usage:
    python scripts/fetch_polyhaven_models.py [id ...]

Without arguments, MODELS is downloaded. Existing files are skipped. Only file names from
Poly Haven's API are used (paths are sanitized), and the license is recorded in assets/LICENSES.md.
"""
import json
import os
import sys
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "assets", "polyhaven")
UA = {"User-Agent": "heimskringla-asset-script"}
RES = "1k"

# Rocks and cliffs for the mountainsides (see PropLibrary: Cliff)
MODELS = ["rock_face_01", "rock_face_02", "mountainside", "boulder_01", "rock_07", "rock_09"]


def get(url):
    with urllib.request.urlopen(urllib.request.Request(url, headers=UA)) as r:
        return r.read()


def safe_rel(path):
    parts = [p for p in path.replace("\\", "/").split("/") if p not in ("", ".", "..")]
    return os.path.join(*parts)


def fetch(asset_id):
    files = json.loads(get(f"https://api.polyhaven.com/files/{asset_id}"))
    gl = files["gltf"][RES]["gltf"]
    dest = os.path.join(OUT, asset_id)
    os.makedirs(dest, exist_ok=True)
    items = [(f"{asset_id}.gltf", gl["url"])] + [(rel, v["url"]) for rel, v in gl.get("include", {}).items()]
    for rel, url in items:
        target = os.path.join(dest, safe_rel(rel))
        if os.path.exists(target) and os.path.getsize(target) > 0:
            continue
        os.makedirs(os.path.dirname(target), exist_ok=True)
        print(f"  {asset_id}: {rel}")
        with open(target, "wb") as f:
            f.write(get(url))


def main():
    ids = sys.argv[1:] or MODELS
    for asset_id in ids:
        print(asset_id)
        fetch(asset_id)
    lic = os.path.join(ROOT, "assets", "LICENSES.md")
    with open(lic, encoding="utf-8") as f:
        text = f.read()
    with open(lic, "a", encoding="utf-8", newline="\n") as f:
        if "Poly Haven" not in text:
            f.write("\n## assets/polyhaven/ - Poly Haven (CC0 1.0)\n\n")
            f.write("Models from https://polyhaven.com, license CC0 1.0 (no attribution required):\n")
        for asset_id in ids:
            if f"polyhaven.com/a/{asset_id}" not in text:
                f.write(f"- {asset_id} - https://polyhaven.com/a/{asset_id}\n")
    print("Done:", OUT)


if __name__ == "__main__":
    main()
