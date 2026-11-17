"""Extracts CC0 model packs and copies a curated selection into the project's assets/ folder.

Usage:
    python scripts/import_assets.py [zip-dir] [extracted-dir]

Default: zip-dir = D:/3dmodels/zip, extracted-dir = D:/3dmodels/extracted

Steps:
  1. Each .zip is extracted to <extracted>/<packname> (skipped if the folder exists).
  2. Selected files are copied to assets/ (glTF + .bin + the textures the glTF references).

Structure of assets/:
  assets/nature/        Quaternius Stylized Nature MegaKit (trees, bushes, rocks, flowers, mushrooms)
  assets/characters/    Quaternius Universal Base Characters (character + hair/beard)
  assets/animations/    Quaternius Universal Animation Library (UAL1_Standard.glb)
  assets/survival/      Kenney Survival Kit (tools, resources, camp) - GLB + Textures/
  assets/kenney_nature/ Kenney Nature Kit - rock pillars and large rocks (shapes for cliff formations)
  assets/outfits/       Quaternius Modular Character Outfits - Fantasy (Peasant/Ranger, same skeleton)
  assets/nature_lowpoly/ Quaternius Ultimate Nature Pack (snowy trees, stumps, mossy rocks) - converted
                        from .blend to .glb with Blender (scripts/blender_batch_export.py)
  assets/LICENSES.md    License overview (all CC0)

The script is idempotent: run it again after adding new packs or changing the selection.
"""
import json
import os
import re
import shutil
import sys
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ASSETS = os.path.join(ROOT, "assets")

# Which MegaKit models are used (regex against file name without extension)
NATURE_MODELS = r"^(CommonTree_\d|Pine_\d|TwistedTree_\d|DeadTree_\d|Bush_Common.*|Fern_1|Plant_1.*|Plant_7.*|" \
                r"Rock_Medium_\d|Pebble_.*|Flower_.*_Group|Mushroom_.*|Clover_\d|Grass_.*)$"
SURVIVAL_MODELS = r"^(tool-.*|resource-.*|tree-log.*|tree-trunk|campfire-.*|workbench.*|chest|barrel|bedroll.*|tent.*|" \
                  r"fence.*|signpost.*|bucket|box.*|structure.*|floor.*|rock-.*|fish.*)$"
CHARACTER_FILES = ["Superhero_Male_FullBody", "Superhero_Female_FullBody"]
OUTFIT_FILES = ["Male_Peasant", "Male_Ranger", "Female_Peasant", "Female_Ranger"]
OUTFIT_PARTS = ["Male_Ranger_Head_Hood", "Female_Ranger_Head_Hood"]
LOWPOLY_MODELS = r"^(BirchTree_\d|BirchTree_Snow_\d|BirchTree_Dead_\d|PineTree_Snow_\d|PineTree_\d|CommonTree_Snow_\d|"                  r"Rock_Moss_\d|Rock_Snow_\d|Rock_\d|TreeStump.*|WoodLog.*|Bush_Snow_\d|BushBerries_\d|Willow_\d|"                  r"Willow_Dead_\d|Lilypad)$"
KENNEY_NATURE_MODELS = r"^(rock_tall[A-J]|rock_large[A-F]|stone_tall[A-J]|stone_large[A-F]|cliff_large_rock|cliff_rock|log|log_large|stump_old.*)$"
BLENDER_CANDIDATES = [
    "C:/Program Files/Blender Foundation/Blender 5.2/blender.exe",
    "C:/Program Files/Blender Foundation/Blender 4.4/blender.exe",
    "blender",
]
HAIR_FILES = ["Hair_Beard", "Hair_Long", "Hair_SimpleParted", "Hair_Buns", "Eyebrows_Regular", "Eyebrows_Female"]


def unpack(zip_dir, out_dir):
    os.makedirs(out_dir, exist_ok=True)
    for name in sorted(os.listdir(zip_dir)):
        if not name.lower().endswith(".zip"):
            continue
        pack = re.sub(r"\[.*?\]", "", os.path.splitext(name)[0]).strip().replace(" ", "_")
        dest = os.path.join(out_dir, pack)
        if os.path.isdir(dest):
            print(f"  (exists) {pack}")
            continue
        print(f"  extracting {name} -> {dest}")
        with zipfile.ZipFile(os.path.join(zip_dir, name)) as z:
            z.extractall(dest)


def find_dir(base, *parts_contains):
    """Find the first folder under base whose path contains all the given parts."""
    for dirpath, _, _ in os.walk(base):
        p = dirpath.replace("\\", "/")
        if all(part in p for part in parts_contains):
            return dirpath
    return None


def copy_gltf(src_path, dest_dir, tex_search_dirs=()):
    """Copies a .gltf with its .bin and textures (images next to it or in the search folders)."""
    os.makedirs(dest_dir, exist_ok=True)
    shutil.copy2(src_path, dest_dir)
    g = json.load(open(src_path, encoding="utf-8"))
    src_dir = os.path.dirname(src_path)
    refs = [b.get("uri") for b in g.get("buffers", [])] + [i.get("uri") for i in g.get("images", [])]
    for uri in refs:
        if not uri or uri.startswith("data:"):
            continue
        # Some packs reference "X_png.png" while the file is named "X.png"
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
            print(f"    WARNING: could not find {uri} for {os.path.basename(src_path)}")


def main():
    zip_dir = sys.argv[1] if len(sys.argv) > 1 else "D:/3dmodels/zip"
    out_dir = sys.argv[2] if len(sys.argv) > 2 else "D:/3dmodels/extracted"
    print("1) Extracting")
    unpack(zip_dir, out_dir)

    print("2) Copying selection to assets/")
    # Nature
    src = find_dir(out_dir, "Stylized_Nature_MegaKit", "/glTF")
    n = 0
    if src:
        for f in sorted(os.listdir(src)):
            if f.endswith(".gltf") and re.match(NATURE_MODELS, f[:-5]):
                copy_gltf(os.path.join(src, f), os.path.join(ASSETS, "nature"))
                n += 1
    print(f"  nature: {n} models")

    # Characters
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
    print(f"  characters/hair: {n} files")

    # Animations
    src = find_dir(out_dir, "Universal_Animation_Library", "Unreal-Godot")
    if src:
        os.makedirs(os.path.join(ASSETS, "animations"), exist_ok=True)
        shutil.copy2(os.path.join(src, "UAL1_Standard.glb"), os.path.join(ASSETS, "animations"))
        print("  animations: UAL1_Standard.glb")

    # Kenney Survival Kit (GLB references Textures/colormap.png)
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
    print(f"  survival: {n} models")

    # Kenney Nature Kit: rock formations (the shape is used, the material is replaced in-game)
    src = find_dir(out_dir, "kenney_nature-kit", "GLTF format")
    n = 0
    if src:
        dest = os.path.join(ASSETS, "kenney_nature")
        os.makedirs(dest, exist_ok=True)
        for f in sorted(os.listdir(src)):
            if f.endswith(".glb") and re.match(KENNEY_NATURE_MODELS, f[:-4]):
                shutil.copy2(os.path.join(src, f), dest)
                n += 1
    print(f"  kenney nature: {n} models")

    # Outfits (complete characters with clothes, same 65-bone skeleton as UAL)
    src = find_dir(out_dir, "Modular_Character_Outfits", "glTF (Godot-Unreal)/Outfits")
    n = 0
    if src:
        for name in OUTFIT_FILES:
            p = os.path.join(src, name + ".gltf")
            if os.path.isfile(p):
                copy_gltf(p, os.path.join(ASSETS, "outfits"))
                n += 1
        parts = os.path.join(os.path.dirname(src), "Modular Parts")
        for name in OUTFIT_PARTS:
            p = os.path.join(parts, name + ".gltf")
            if os.path.isfile(p):
                copy_gltf(p, os.path.join(ASSETS, "outfits"))
                n += 1
    print(f"  outfits: {n} files")

    # Ultimate Nature Pack: .blend/FBX only -> convert with Blender
    src = find_dir(out_dir, "Ultimate_Nature_Pack", "/Blends")
    if src:
        import subprocess
        blender = next((b for b in BLENDER_CANDIDATES if b == "blender" or os.path.isfile(b)), None)
        script = os.path.join(ROOT, "scripts", "blender_batch_export.py")
        try:
            r = subprocess.run([blender, "-b", "--factory-startup", "--python", script, "--", src,
                                os.path.join(ASSETS, "nature_lowpoly"), LOWPOLY_MODELS],
                               capture_output=True, text=True, timeout=900)
            last = [l for l in r.stdout.splitlines() if "done:" in l]
            print("  lowpoly: " + (last[-1] if last else "Blender produced no output"))
        except Exception as e:  # noqa: BLE001
            print(f"  lowpoly: skipped ({e}) - install Blender or run blender_batch_export.py manually")

    with open(os.path.join(ASSETS, "LICENSES.md"), "w", encoding="utf-8") as f:
        f.write("# Licenses for third-party assets\n\n"
                "All packs are CC0 1.0 (public domain). Attribution is not required, but appreciated.\n\n"
                "| Folder | Pack | Creator |\n|---|---|---|\n"
                "| nature/ | Stylized Nature MegaKit | Quaternius (quaternius.com) |\n"
                "| characters/ | Universal Base Characters | Quaternius |\n"
                "| animations/ | Universal Animation Library | Quaternius |\n"
                "| survival/ | Survival Kit | Kenney (kenney.nl) |\n"
                "| outfits/ | Modular Character Outfits - Fantasy | Quaternius |\n"
                "| nature_lowpoly/ | Ultimate Nature Pack (Jun 2019) | Quaternius |\n"
                "| kenney_nature/ | Nature Kit | Kenney (kenney.nl) |\n\n"
                "All packs are also available extracted in D:/3dmodels/extracted.\n")
    print("Done.")


if __name__ == "__main__":
    main()
