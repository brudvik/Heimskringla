"""Blender script: exports .blend files to .glb (for models that only exist as .blend/FBX).

Runs inside Blender (background mode):
    blender -b --factory-startup --python scripts/blender_batch_export.py -- <srcdir> <outdir> [regex]

Example:
    "C:/Program Files/Blender Foundation/Blender 5.2/blender.exe" -b --factory-startup \
        --python scripts/blender_batch_export.py -- \
        "D:/3dmodels/extracted/Ultimate_Nature_Pack.../Blends" assets/nature_lowpoly "^(BirchTree_\\d|TreeStump.*)$"

- Applies modifiers (export_apply), keeps material colors.
- Skips files where the .glb is already newer than the .blend (delete the .glb to force a re-export).
- Copies material.diffuse_color into the Principled BSDF (older .blend files otherwise lose their colors).
- Connects the Principled BSDF to a single active Material Output (removes Diffuse BSDF / extra outputs).
"""
import os
import re
import sys

import bpy

argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
if len(argv) < 2:
    print(__doc__)
    sys.exit(1)
src, out = argv[0], argv[1]
pattern = re.compile(argv[2]) if len(argv) > 2 else None
os.makedirs(out, exist_ok=True)

done = skipped = 0
for name in sorted(os.listdir(src)):
    if not name.lower().endswith(".blend"):
        continue
    base = os.path.splitext(name)[0]
    if pattern and not pattern.match(base):
        continue
    src_path = os.path.join(src, name)
    dst_path = os.path.join(out, base + ".glb")
    if os.path.exists(dst_path) and os.path.getmtime(dst_path) >= os.path.getmtime(src_path):
        skipped += 1
        continue
    bpy.ops.wm.open_mainfile(filepath=src_path)
    # Remove cameras/lights that don't belong in a model
    for obj in list(bpy.data.objects):
        if obj.type in {"CAMERA", "LIGHT"}:
            bpy.data.objects.remove(obj, do_unlink=True)
    # Older .blend files store the color in material.diffuse_color; glTF reads the Principled BSDF.
    for mat in bpy.data.materials:
        col = tuple(mat.diffuse_color)
        if not mat.use_nodes:
            mat.use_nodes = True
        bsdf = next((nd for nd in mat.node_tree.nodes if nd.type == "BSDF_PRINCIPLED"), None)
        if bsdf is None:
            continue
        base = bsdf.inputs["Base Color"]
        if not base.is_linked and tuple(base.default_value)[:3] in ((0.8, 0.8, 0.8), (1.0, 1.0, 1.0)) \
                and col[:3] != (0.8, 0.8, 0.8):
            base.default_value = col
        if "Roughness" in bsdf.inputs:
            bsdf.inputs["Roughness"].default_value = 0.9
        # Some files have two Material Output nodes where the active one goes to a Diffuse BSDF
        # (not supported by glTF). Connect Principled to a single active output and remove the rest.
        nodes = mat.node_tree.nodes
        outputs = [nd for nd in nodes if nd.type == "OUTPUT_MATERIAL"]
        for nd in [nd for nd in nodes if nd.type == "BSDF_DIFFUSE"]:
            nodes.remove(nd)
        if outputs:
            keep = outputs[0]
            for extra in outputs[1:]:
                nodes.remove(extra)
            keep.is_active_output = True
            keep.target = "ALL"   # the exporter ignores outputs with target EEVEE/CYCLES
            mat.node_tree.links.new(bsdf.outputs["BSDF"], keep.inputs["Surface"])
    bpy.ops.export_scene.gltf(filepath=dst_path, export_format="GLB", export_apply=True, export_yup=True)
    done += 1
    print(f"[export] {base}.glb")

print(f"[export] done: {done} exported, {skipped} unchanged")
