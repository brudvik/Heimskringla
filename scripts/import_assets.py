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
  assets/kenney_nature/ Kenney Nature Kit - steinsøyler og store steiner (former til klippeformasjoner)
  assets/outfits/       Quaternius Modular Character Outfits - Fantasy (Peasant/Ranger, samme skjelett)
  assets/nature_lowpoly/ Quaternius Ultimate Nature Pack (snøtrær, stubber, mosesteiner) - konvertert
                        fra .blend til .glb med Blender (scripts/blender_batch_export.py)
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

    # Kenney Nature Kit: steinformasjoner (formen brukes, materialet byttes i spillet)
    src = find_dir(out_dir, "kenney_nature-kit", "GLTF format")
    n = 0
    if src:
        dest = os.path.join(ASSETS, "kenney_nature")
        os.makedirs(dest, exist_ok=True)
        for f in sorted(os.listdir(src)):
            if f.endswith(".glb") and re.match(KENNEY_NATURE_MODELS, f[:-4]):
                shutil.copy2(os.path.join(src, f), dest)
                n += 1
    print(f"  kenney natur: {n} modeller")

    # Antrekk (komplette figurer med klær, samme 65-beins skjelett som UAL)
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
    print(f"  antrekk: {n} filer")

    # Ultimate Nature Pack: kun .blend/FBX -> konverter med Blender
    src = find_dir(out_dir, "Ultimate_Nature_Pack", "/Blends")
    if src:
        import subprocess
        blender = next((b for b in BLENDER_CANDIDATES if b == "blender" or os.path.isfile(b)), None)
        script = os.path.join(ROOT, "scripts", "blender_batch_export.py")
        try:
            r = subprocess.run([blender, "-b", "--factory-startup", "--python", script, "--", src,
                                os.path.join(ASSETS, "nature_lowpoly"), LOWPOLY_MODELS],
                               capture_output=True, text=True, timeout=900)
            last = [l for l in r.stdout.splitlines() if "ferdig" in l]
            print("  lowpoly: " + (last[-1] if last else "Blender ga ingen utdata"))
        except Exception as e:  # noqa: BLE001
            print(f"  lowpoly: hoppet over ({e}) - installer Blender eller kjør blender_batch_export.py manuelt")

    with open(os.path.join(ASSETS, "LICENSES.md"), "w", encoding="utf-8") as f:
        f.write("# Lisenser for tredjeparts-assets\n\n"
                "Alle pakker er CC0 1.0 (public domain). Kreditering er ikke påkrevd, men gis gjerne.\n\n"
                "| Mappe | Pakke | Skaper |\n|---|---|---|\n"
                "| nature/ | Stylized Nature MegaKit | Quaternius (quaternius.com) |\n"
                "| characters/ | Universal Base Characters | Quaternius |\n"
                "| animations/ | Universal Animation Library | Quaternius |\n"
                "| survival/ | Survival Kit | Kenney (kenney.nl) |\n"
                "| outfits/ | Modular Character Outfits - Fantasy | Quaternius |\n"
                "| nature_lowpoly/ | Ultimate Nature Pack (Jun 2019) | Quaternius |\n"
                "| kenney_nature/ | Nature Kit | Kenney (kenney.nl) |\n\n"
                "Alle pakker ligger også utpakket i D:/3dmodels/extracted.\n")
    print("Ferdig.")


if __name__ == "__main__":
    main()
