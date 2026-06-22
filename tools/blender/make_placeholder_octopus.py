"""Generate a rigged placeholder octopus glb for KhaozEngine.

A throwaway stand-in: a body blob plus N tapered tentacles, each a single bone
chain, skinned with automatic weights, exported as a glTF binary. Its job is to
validate the engine's skinned-glTF import + rig-naming contract and to give the
boss fight something to develop against BEFORE a real sculpted asset exists. It
is deliberately ugly; do not ship it.

The bone layout matches docs/specs/realistic-tentacle-boss.md:
  - one root bone "body" at the origin
  - per tentacle i: a chain "tentacle.<i>.<j>" for j in 0..bones-1, child of "body"
The per-tentacle bone count must equal the ProceduralChainSolver spine length the
game drives it with (default 8). Total bones stay well under the 128/draw cap.

Run headless (Blender 3.6+; the glTF exporter ships with Blender):
  blender --background --python tools/blender/make_placeholder_octopus.py -- \
      --out placeholder_octopus.glb --tentacles 4 --bones 8

Only depends on Blender's bundled bpy + the glTF exporter; no extra packages.
"""

import bpy
import bmesh
import math
import sys
from mathutils import Vector, Matrix


def parse_args():
    argv = sys.argv
    argv = argv[argv.index("--") + 1:] if "--" in argv else []
    out = "placeholder_octopus.glb"
    tentacles = 4
    bones = 8
    body_radius = 0.9
    tentacle_length = 3.2
    tentacle_radius = 0.28
    i = 0
    while i < len(argv):
        a = argv[i]
        if a == "--out":
            out = argv[i + 1]; i += 2
        elif a == "--tentacles":
            tentacles = int(argv[i + 1]); i += 2
        elif a == "--bones":
            bones = int(argv[i + 1]); i += 2
        elif a == "--body-radius":
            body_radius = float(argv[i + 1]); i += 2
        elif a == "--tentacle-length":
            tentacle_length = float(argv[i + 1]); i += 2
        elif a == "--tentacle-radius":
            tentacle_radius = float(argv[i + 1]); i += 2
        else:
            i += 1
    return out, tentacles, bones, body_radius, tentacle_length, tentacle_radius


def clear_scene():
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)
    for block in (bpy.data.meshes, bpy.data.armatures, bpy.data.objects):
        for item in list(block):
            try:
                block.remove(item)
            except Exception:
                pass


def make_body(radius):
    bpy.ops.mesh.primitive_uv_sphere_add(radius=radius, segments=24, ring_count=16, location=(0, 0, 0))
    body = bpy.context.active_object
    body.name = "octopus"
    # squash slightly so it reads as a mantle, not a ball
    body.scale = (1.0, 1.0, 0.8)
    bpy.ops.object.transform_apply(scale=True)
    return body


def tentacle_dir(index, count):
    """Even radial splay around +Z, tentacles fan outward and slightly down."""
    ang = (index / count) * math.tau
    return Vector((math.cos(ang), math.sin(ang), -0.15)).normalized()


def add_tentacle_geometry(body, index, count, length, radius, body_radius):
    """Append a tapered tube of `length` to the body mesh, rooted at the rim."""
    me = body.data
    bm = bmesh.new()
    bm.from_mesh(me)

    base = tentacle_dir(index, count) * (body_radius * 0.85)
    axis = tentacle_dir(index, count)
    # build a perpendicular frame
    up = Vector((0, 0, 1))
    side = axis.cross(up)
    if side.length < 1e-4:
        side = Vector((1, 0, 0))
    side.normalize()
    other = axis.cross(side).normalized()

    rings = 10
    radial = 8
    prev_ring = None
    for r in range(rings + 1):
        t = r / rings
        centre = base + axis * (length * t)
        rr = radius * (1.0 - 0.85 * t)  # taper to a thin tip
        ring = []
        for s in range(radial):
            a = (s / radial) * math.tau
            p = centre + (side * math.cos(a) + other * math.sin(a)) * rr
            ring.append(bm.verts.new(p))
        bm.verts.ensure_lookup_table()
        if prev_ring is not None:
            for s in range(radial):
                s2 = (s + 1) % radial
                bm.faces.new((prev_ring[s], prev_ring[s2], ring[s2], ring[s]))
        prev_ring = ring

    bm.normal_update()
    bm.to_mesh(me)
    bm.free()


def build_armature(count, bones, length, body_radius):
    arm_data = bpy.data.armatures.new("octopus_rig")
    arm = bpy.data.objects.new("octopus_rig", arm_data)
    bpy.context.collection.objects.link(arm)
    bpy.context.view_layer.objects.active = arm
    bpy.ops.object.mode_set(mode="EDIT")

    eb = arm_data.edit_bones
    root = eb.new("body")
    root.head = Vector((0, 0, 0))
    root.tail = Vector((0, 0, 0.4))

    seg = length / bones
    for i in range(count):
        axis = tentacle_dir(i, count)
        base = axis * (body_radius * 0.85)
        parent = root
        for j in range(bones):
            b = eb.new(f"tentacle.{i}.{j}")
            b.head = base + axis * (seg * j)
            b.tail = base + axis * (seg * (j + 1))
            b.parent = parent
            b.use_connect = (j > 0)
            parent = b

    bpy.ops.object.mode_set(mode="OBJECT")
    return arm


def skin(body, arm):
    body.select_set(True)
    arm.select_set(True)
    bpy.context.view_layer.objects.active = arm
    # automatic weights: good enough for a placeholder; a real asset hand-weights.
    bpy.ops.object.parent_set(type="ARMATURE_AUTO")


def export_glb(path):
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.export_scene.gltf(
        filepath=path,
        export_format="GLB",
        export_yup=True,
        export_skins=True,
        export_apply=True,
        use_selection=False,
    )


def main():
    out, tentacles, bones, body_radius, length, t_radius = parse_args()
    clear_scene()
    body = make_body(body_radius)
    for i in range(tentacles):
        add_tentacle_geometry(body, i, tentacles, length, t_radius, body_radius)
    arm = build_armature(tentacles, bones, length, body_radius)
    skin(body, arm)
    export_glb(out)
    print(f"[make_placeholder_octopus] wrote {out}: {tentacles} tentacles x {bones} bones")


if __name__ == "__main__":
    main()
