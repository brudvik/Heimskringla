"""Pakker ut CC0-modellpakker og kopierer et kuratert utvalg inn i prosjektets assets/-mappe.

Bruk:
    python scripts/import_assets.py [zip-mappe] [utpakket-mappe]

Standard: zip-mappe = D:/3dmodels/zip, utpakket-mappe = D:/3dmodels/extracted

Steg:
  1. Hver .zip pakkes ut til <utpakket>/<pakkenavn> (hoppes over hvis mappen finnes).
  2. Utvalgte filer kopieres til assets/ (glTF + .bin + teksturer som glTF-en refererer til).

Strukturen i assets/:
  assets/nature/        Quaternius Stylized Nature MegaKit (trær, busker, steiner, blomster, sopp)
  assets/characters/    Quaternius Universal Base Characters (figur + hår/skjegg)
  assets/animations/    Quaternius Universal Animation Library (UAL1_Standard.glb)
  assets/survival/      Kenney Survival Kit (verktøy, ressurser, leir) - GLB + Textures/
  assets/LICENSES.md    Lisensoversikt (alle CC0)

Skriptet er idempotent: kjør det igjen etter at du har lagt til nye pakker eller endret utvalget.
"""
import json
import os
import re
import shutil
import sys
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ASSETS = os.path.join(ROOT, "assets")

# Hvilke MegaKit-modeller som brukes (regex mot filnavn uten endelse)
NATURE_MODELS = r"^(CommonTree_\d|Pine_\d|TwistedTree_\d|DeadTree_\d|Bush_Common.*|Fern_1|Plant_1.*|Plant_7.*|" \
                r"Rock_Medium_\d|Pebble_.*|Flower_.*_Group|Mushroom_.*|Clover_\d|Grass_.*)$"
SURVIVAL_MODELS = r"^(tool-.*|resource-.*|tree-log.*|tree-trunk|campfire-.*|workbench.*|chest|barrel|bedroll.*|tent.*|" \
                  r"fence.*|signpost.*|bucket|box.*|structure.*|floor.*|rock-.*|fish.*)$"
CHARACTER_FILES = ["Superhero_Male_FullBody", "Superhero_Female_FullBody"]
HAIR_FILES = ["Hair_Beard", "Hair_Long", "Hair_SimpleParted", "Hair_Buns", "Eyebrows_Regular", "Eyebrows_Female"]


def unpack(zip_dir, out_dir):
    os.makedirs(out_dir, exist_ok=True)
    for name in sorted(os.listdir(zip_dir)):
        if not name.lower().endswith(".zip"):
            continue
        pack = re.sub(r"\[.*?\]", "", os.path.splitext(name)[0]).strip().replace(" ", "_")
        dest = os.path.join(out_dir, pack)
        if os.path.isdir(dest):
            print(f"  (finnes) {pack}")
            continue
        print(f"  pakker ut {name} -> {dest}")
        with zipfile.ZipFile(os.path.join(zip_dir, name)) as z:
            z.extractall(dest)


def find_dir(base, *parts_contains):
    """Finn første mappe under base hvis sti inneholder alle gitte deler."""
    for dirpath, _, _ in os.walk(base):
        p = dirpath.replace("\\", "/")
        if all(part in p for part in parts_contains):
            return dirpath
    return None


def copy_gltf(src_path, dest_dir, tex_search_dirs=()):
    """Kopierer en .gltf med tilhørende .bin og teksturer (bilder ved siden av eller i søkemapper)."""
    os.makedirs(dest_dir, exist_ok=True)
    shutil.copy2(src_path, dest_dir)
    g = json.load(open(src_path, encoding="utf-8"))
    src_dir = os.path.dirname(src_path)
    refs = [b.get("uri") for b in g.get("buffers", [])] + [i.get("uri") for i in g.get("images", [])]
    for uri in refs:
        if not uri or uri.startswith("data:"):
            continue
        # Noen pakker refererer "X_png.png" mens filen heter "X.png"
        names = [uri] + ([uri.replace("_png.png", ".png")] if uri.endswith("_png.png") else [])
        candidates = [os.path.join(d, nm) for nm in names for d in (src_dir, *tex_search_dirs)]
        for cand in candidates:
            if os.path.isfile(cand):
                target = os.path.join(dest_dir, uri)
                os.makedirs(os.path.dirname(target), exist_ok=True)
                if not os.path.exists(target):
                    shutil.copy2(cand, target)
                break
        else:
            print(f"    ADVARSEL: fant ikke {uri} for {os.path.basename(src_path)}")


def main():
    zip_dir = sys.argv[1] if len(sys.argv) > 1 else "D:/3dmodels/zip"
    out_dir = sys.argv[2] if len(sys.argv) > 2 else "D:/3dmodels/extracted"
    print("1) Pakker ut")
    unpack(zip_dir, out_dir)

    print("2) Kopierer utvalg til assets/")
    # Natur
    src = find_dir(out_dir, "Stylized_Nature_MegaKit", "/glTF")
    n = 0
    if src:
        for f in sorted(os.listdir(src)):
            if f.endswith(".gltf") and re.match(NATURE_MODELS, f[:-5]):
                copy_gltf(os.path.join(src, f), os.path.join(ASSETS, "nature"))
                n += 1
    print(f"  natur: {n} modeller")

    # Figurer
    src = find_dir(out_dir, "Universal_Base_Characters", "Base Characters/Godot")
    n = 0
    if src:
        tex = os.path.join(os.path.dirname(src), "Textures")
        for name in CHARACTER_FILES:
            p = os.path.join(src, name + ".gltf")
            if os.path.isfile(p):
                copy_gltf(p, os.path.join(ASSETS, "characters"), (tex,))
                n += 1
    src = find_dir(out_dir, "Universal_Base_Characters", "Rigged to Head Bone/glTF")
    if src:
        for name in HAIR_FILES:
            p = os.path.join(src, name + ".gltf")
            if os.path.isfile(p):
                copy_gltf(p, os.path.join(ASSETS, "characters"))
                n += 1
    print(f"  figurer/hår: {n} filer")

    # Animasjoner
    src = find_dir(out_dir, "Universal_Animation_Library", "Unreal-Godot")
    if src:
        os.makedirs(os.path.join(ASSETS, "animations"), exist_ok=True)
        shutil.copy2(os.path.join(src, "UAL1_Standard.glb"), os.path.join(ASSETS, "animations"))
        print("  animasjoner: UAL1_Standard.glb")

    # Kenney Survival Kit (GLB refererer til Textures/colormap.png)
    src = find_dir(out_dir, "kenney_survival-kit", "GLB format")
    n = 0
    if src:
        dest = os.path.join(ASSETS, "survival")
        os.makedirs(os.path.join(dest, "Textures"), exist_ok=True)
        for f in sorted(os.listdir(src)):
            if f.endswith(".glb") and re.match(SURVIVAL_MODELS, f[:-4]):
                shutil.copy2(os.path.join(src, f), dest)
                n += 1
        for t in os.listdir(os.path.join(src, "Textures")):
            shutil.copy2(os.path.join(src, "Textures", t), os.path.join(dest, "Textures"))
    print(f"  survival: {n} modeller")

    with open(os.path.join(ASSETS, "LICENSES.md"), "w", encoding="utf-8") as f:
        f.write("# Lisenser for tredjeparts-assets\n\n"
                "Alle pakker er CC0 1.0 (public domain). Kreditering er ikke påkrevd, men gis gjerne.\n\n"
                "| Mappe | Pakke | Skaper |\n|---|---|---|\n"
                "| nature/ | Stylized Nature MegaKit | Quaternius (quaternius.com) |\n"
                "| characters/ | Universal Base Characters | Quaternius |\n"
                "| animations/ | Universal Animation Library | Quaternius |\n"
                "| survival/ | Survival Kit | Kenney (kenney.nl) |\n\n"
                "Ubrukte pakker (f.eks. Kenney Nature Kit) ligger utpakket i D:/3dmodels/extracted.\n")
    print("Ferdig.")


if __name__ == "__main__":
    main()
