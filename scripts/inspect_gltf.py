"""Shows the contents of glTF/GLB files: nodes, meshes, triangles, size, materials, textures,
skeleton (bone names) and animations.

Usage:
    python scripts/inspect_gltf.py <file or folder> [--bones] [--anims]

Example:
    python scripts/inspect_gltf.py D:/3dmodels/extracted/Stylized_Nature_MegaKit/glTF
    python scripts/inspect_gltf.py assets/characters/Superhero_Male_FullBody.gltf --bones
"""
import glob
import json
import os
import struct
import sys


def load(path):
    if path.lower().endswith(".glb"):
        data = open(path, "rb").read()
        length = struct.unpack_from("<I", data, 12)[0]
        return json.loads(data[20:20 + length])
    return json.load(open(path, encoding="utf-8"))


def describe(path, bones=False, anims=False):
    g = load(path)
    mins, maxs = [1e9] * 3, [-1e9] * 3
    prims = tris = 0
    for m in g.get("meshes", []):
        for p in m["primitives"]:
            prims += 1
            a = g["accessors"][p["attributes"]["POSITION"]]
            for i in range(3):
                mins[i] = min(mins[i], a["min"][i])
                maxs[i] = max(maxs[i], a["max"][i])
            if "indices" in p:
                tris += g["accessors"][p["indices"]]["count"] // 3
    size = tuple(round(maxs[i] - mins[i], 2) for i in range(3)) if prims else None
    mats = [m.get("name") for m in g.get("materials", [])]
    imgs = [i.get("uri", "<embedded>") for i in g.get("images", [])]
    print(f"{os.path.basename(path):40} nodes={len(g.get('nodes', []))} meshes={len(g.get('meshes', []))} "
          f"prims={prims} tris={tris} size={size} miny={round(mins[1], 2) if prims else '-'} "
          f"skins={len(g.get('skins', []))} anims={len(g.get('animations', []))}")
    print(f"    materials={mats} images={imgs}")
    if bones:
        for s in g.get("skins", []):
            names = [g["nodes"][j].get("name") for j in s["joints"]]
            print(f"    bones ({len(names)}): {names}")
        roots = [g["nodes"][i].get("name") for i in g["scenes"][g.get("scene", 0)]["nodes"]]
        print(f"    root nodes: {roots}")
    if anims:
        print(f"    animations: {[a.get('name') for a in g.get('animations', [])]}")


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    bones = "--bones" in sys.argv
    anims = "--anims" in sys.argv
    if not args:
        print(__doc__)
        return
    for target in args:
        files = sorted(glob.glob(os.path.join(target, "*.gltf")) + glob.glob(os.path.join(target, "*.glb"))) \
            if os.path.isdir(target) else [target]
        for f in files:
            describe(f, bones, anims)


if __name__ == "__main__":
    main()
