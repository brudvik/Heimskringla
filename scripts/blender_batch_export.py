"""Blender-skript: eksporterer .blend-filer til .glb (for modeller som bare finnes som .blend/FBX).

Kjøres inne i Blender (bakgrunnsmodus):
    blender -b --factory-startup --python scripts/blender_batch_export.py -- <kildemappe> <utmappe> [regex]

Eksempel:
    "C:/Program Files/Blender Foundation/Blender 5.2/blender.exe" -b --factory-startup \
        --python scripts/blender_batch_export.py -- \
        "D:/3dmodels/extracted/Ultimate_Nature_Pack.../Blends" assets/nature_lowpoly "^(BirchTree_\\d|TreeStump.*)$"

- Bruker modifikatorer (export_apply), beholder materialfarger.
- Hopper over filer der .glb allerede er nyere enn .blend (slett .glb for å tvinge ny eksport).
- Kopierer material.diffuse_color inn i Principled BSDF (eldre .blend-filer mister ellers fargene).
- Kobler Principled BSDF til én aktiv Material Output (fjerner Diffuse BSDF / ekstra outputs).
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
    # Fjern kamera/lys som ikke hører hjemme i en modell
    for obj in list(bpy.data.objects):
        if obj.type in {"CAMERA", "LIGHT"}:
            bpy.data.objects.remove(obj, do_unlink=True)
    # Eldre .blend-filer lagrer fargen i material.diffuse_color; glTF leser Principled BSDF.
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
        # Noen filer har to Material Output-noder der den aktive går til en Diffuse BSDF
        # (støttes ikke av glTF). Koble Principled til én aktiv output og fjern resten.
        nodes = mat.node_tree.nodes
        outputs = [nd for nd in nodes if nd.type == "OUTPUT_MATERIAL"]
        for nd in [nd for nd in nodes if nd.type == "BSDF_DIFFUSE"]:
            nodes.remove(nd)
        if outputs:
            keep = outputs[0]
            for extra in outputs[1:]:
                nodes.remove(extra)
            keep.is_active_output = True
            keep.target = "ALL"   # eksportøren ignorerer outputs med target EEVEE/CYCLES
            mat.node_tree.links.new(bsdf.outputs["BSDF"], keep.inputs["Surface"])
    bpy.ops.export_scene.gltf(filepath=dst_path, export_format="GLB", export_apply=True, export_yup=True)
    done += 1
    print(f"[export] {base}.glb")

print(f"[export] ferdig: {done} eksportert, {skipped} uendret")
