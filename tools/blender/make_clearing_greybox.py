"""Build the greybox "forest clearing at a mountain base" terrain in Blender.

A throwaway, fully-procedural stand-in for an MMO overworld set-piece. Its job
is to validate LAYOUT and SCALE before any real art exists, and to prove the
authoring -> bake path the engine terrain system will consume: the whole scene
is a heightfield plus an instance scatter list, which is exactly what bakes to a
per-cell heightmap + placement list later. It is deliberately ugly low-poly; do
not ship it.

1 Blender unit = 1 metre. The scene contains:
  - Clearing_Terrain : a displaced grid heightfield (grass -> dirt -> rock ->
    snow by altitude). Mountains rise gradually toward +Y (no vertical wall); a
    shallow basin is carved for the lake.
  - a scattered conifer forest ring (linked duplicates of one TreeTemplate mesh,
    i.e. GPU-instancing-friendly: edit the template, every tree updates)
  - HeroTree : a ~3x landmark conifer with a thick trunk at the clearing centre
  - Lake : a flat water disc sitting in the carved basin
  - Human_1m8 : a 1.8 m reference figure so scale reads at a glance
  - Sun / MMOWorld sky / MMO_Cam framing the establishing shot

Reproducible: same --seed always yields the same forest (deterministic scatter).
Non-destructive: only removes the three factory-default objects (Cube/Light/
Camera) and manages its own "MMO_Clearing" collection, so it is safe to run in
an existing Blender session as well as headless.

Run headless (renders a preview PNG when --out is given):
  blender --background --python tools/blender/make_clearing_greybox.py -- \
      --out clearing.png --seed 5

Or paste the body into Blender's Scripting tab and Run. Tweak CFG to taste.
Only depends on Blender's bundled bpy/mathutils; no extra packages.
"""

import bpy
import math
import random
import sys
from mathutils import Vector, noise

# ---------------------------------------------------------------- config knobs
CFG = dict(
    seed=5,
    tile_m=120.0, subdiv=260,            # square terrain tile + grid resolution
    clearing_radius_m=26.0,              # open radius kept free of scatter trees
    # mountains: gradual ramp from y_start..y_full, no cliff "wall" term
    mtn_y_start=22.0, mtn_y_full=74.0, mtn_base_m=34.0, mtn_detail_m=22.0,
    # lake basin carved into the clearing floor + the water surface level
    lake_center=(-13.0, -2.0), lake_radius_m=8.0, lake_depth_m=3.6, lake_level_m=-1.2,
    hero_at=(0.0, 0.0),                  # landmark tree at the clearing centre
    human_at=(0.0, -6.0),               # 1.8 m scale reference
    forest_keep=0.55, forest_step_m=4.5,  # scatter density / grid spacing
)


def parse_args():
    argv = sys.argv
    argv = argv[argv.index("--") + 1:] if "--" in argv else []
    out = None
    i = 0
    while i < len(argv):
        a = argv[i]
        if a == "--out" and i + 1 < len(argv):
            out = argv[i + 1]; i += 2; continue
        if a == "--seed" and i + 1 < len(argv):
            CFG["seed"] = int(argv[i + 1]); i += 2; continue
        i += 1
    return out


def ss(a, b, x):
    """Smoothstep — gives a gentle toe at the base, which is what kills the wall."""
    t = max(0.0, min(1.0, (x - a) / (b - a)))
    return t * t * (3 - 2 * t)


def height(x, y):
    c = CFG
    g = 1.5 * noise.noise(Vector((x * 0.02, y * 0.02, 0.0)))           # gentle ground roll
    mask = ss(c["mtn_y_start"], c["mtn_y_full"], y)                    # mountain toward +Y
    detail = noise.turbulence(Vector((x * 0.03, y * 0.03, 0.0)), 4, False)
    h = g + mask * (c["mtn_base_m"] + c["mtn_detail_m"] * detail)
    lx, ly = c["lake_center"]
    d = math.hypot(x - lx, y - ly)
    basin = (1.0 - ss(c["lake_radius_m"] * 0.45, c["lake_radius_m"] * 1.30, d)) * (-c["lake_depth_m"])
    return h + basin


def reset_collection():
    scene = bpy.context.scene
    for nm in ("Cube", "Light", "Camera"):
        ob = bpy.data.objects.get(nm)
        if ob:
            bpy.data.objects.remove(ob, do_unlink=True)
    coll = bpy.data.collections.get("MMO_Clearing")
    if coll is None:
        coll = bpy.data.collections.new("MMO_Clearing")
        scene.collection.children.link(coll)
    for ob in list(coll.objects):
        bpy.data.objects.remove(ob, do_unlink=True)
    for c in bpy.context.view_layer.layer_collection.children:
        if c.collection == coll:
            bpy.context.view_layer.active_layer_collection = c
    return coll


def height_ramp_material():
    mat = bpy.data.materials.new("Ground"); mat.use_nodes = True
    nt = mat.node_tree
    for n in list(nt.nodes):
        nt.nodes.remove(n)
    out = nt.nodes.new("ShaderNodeOutputMaterial")
    bsdf = nt.nodes.new("ShaderNodeBsdfPrincipled")
    geo = nt.nodes.new("ShaderNodeNewGeometry")
    sep = nt.nodes.new("ShaderNodeSeparateXYZ")
    mr = nt.nodes.new("ShaderNodeMapRange")
    ramp = nt.nodes.new("ShaderNodeValToRGB")
    el = ramp.color_ramp.elements
    el[0].position = 0.0; el[0].color = (0.27, 0.42, 0.18, 1)   # meadow
    el[1].position = 1.0; el[1].color = (0.93, 0.94, 0.96, 1)   # snow
    for pos, col in [(0.34, (0.20, 0.34, 0.14, 1)),            # darker grass
                     (0.55, (0.34, 0.30, 0.24, 1)),            # dirt
                     (0.76, (0.44, 0.42, 0.40, 1))]:           # rock
        e = ramp.color_ramp.elements.new(pos); e.color = col
    bsdf.inputs["Roughness"].default_value = 0.95
    nt.links.new(geo.outputs["Position"], sep.inputs["Vector"])
    nt.links.new(sep.outputs["Z"], mr.inputs["Value"])
    nt.links.new(mr.outputs["Result"], ramp.inputs["Fac"])
    nt.links.new(ramp.outputs["Color"], bsdf.inputs["Base Color"])
    nt.links.new(bsdf.outputs["BSDF"], out.inputs["Surface"])
    return mat, mr


def simple_material(name, color, roughness=0.9):
    m = bpy.data.materials.new(name); m.use_nodes = True
    p = m.node_tree.nodes["Principled BSDF"]
    p.inputs["Base Color"].default_value = color
    p.inputs["Roughness"].default_value = roughness
    return m


def make_conifer(name, trunk_r, trunk_h, cones, bark, leaf):
    """Trunk cylinder + overlapping foliage cones, joined, origin moved to base."""
    scene = bpy.context.scene
    parts = []
    bpy.ops.mesh.primitive_cylinder_add(vertices=10, radius=trunk_r, depth=trunk_h, location=(0, 0, trunk_h / 2))
    trunk = bpy.context.active_object; trunk.data.materials.append(bark); parts.append(trunk)
    for z, r, h in cones:
        bpy.ops.mesh.primitive_cone_add(vertices=14, radius1=r, radius2=0.0, depth=h, location=(0, 0, z))
        cone = bpy.context.active_object; cone.data.materials.append(leaf)
        for p in cone.data.polygons:
            p.use_smooth = True
        parts.append(cone)
    # select EVERY part before join (primitive_add deselects the previous one)
    bpy.ops.object.select_all(action="DESELECT")
    for o in parts:
        o.select_set(True)
    bpy.context.view_layer.objects.active = trunk
    bpy.ops.object.join()
    ob = bpy.context.active_object; ob.name = name
    scene.cursor.location = (0, 0, 0)
    bpy.ops.object.origin_set(type="ORIGIN_CURSOR")   # base sits ON the ground
    return ob


def build():
    random.seed(CFG["seed"])
    scene = bpy.context.scene
    coll = reset_collection()

    # --- terrain --------------------------------------------------------
    bpy.ops.mesh.primitive_grid_add(x_subdivisions=CFG["subdiv"], y_subdivisions=CFG["subdiv"], size=CFG["tile_m"])
    ground = bpy.context.active_object; ground.name = "Clearing_Terrain"
    for v in ground.data.vertices:
        v.co.z = height(v.co.x, v.co.y)
    ground.data.update()
    for p in ground.data.polygons:
        p.use_smooth = True
    zs = [v.co.z for v in ground.data.vertices]
    zmin, zmax = min(zs), max(zs)
    mat, mr = height_ramp_material()
    mr.inputs["From Min"].default_value = zmin
    mr.inputs["From Max"].default_value = zmax
    ground.data.materials.append(mat)

    # --- lake water disc -----------------------------------------------
    lx, ly = CFG["lake_center"]
    bpy.ops.mesh.primitive_circle_add(vertices=48, radius=CFG["lake_radius_m"], fill_type="NGON",
                                      location=(lx, ly, CFG["lake_level_m"]))
    lake = bpy.context.active_object; lake.name = "Lake"
    lake.data.materials.append(simple_material("Water", (0.04, 0.16, 0.30, 1), roughness=0.04))

    # --- forest + hero tree --------------------------------------------
    bark = simple_material("Bark", (0.20, 0.12, 0.06, 1))
    leaf = simple_material("Leaf", (0.10, 0.28, 0.12, 1))
    template = make_conifer("TreeTemplate", 0.25, 3.0,
                            [(3.0, 2.3, 4.2), (5.0, 1.7, 3.6), (6.7, 1.05, 2.8)], bark, leaf)
    template.hide_render = True

    CR = CFG["clearing_radius_m"]; step = CFG["forest_step_m"]; placed = 0
    for ix in range(-13, 14):
        for iy in range(-13, 4):
            x = ix * step + random.uniform(-1.6, 1.6)
            y = iy * step + random.uniform(-1.6, 1.6)
            if abs(x) > 58 or y < -58 or y > 16:
                continue
            if math.hypot(x, y) < CR:
                continue
            z = height(x, y)
            if z > 6.0:                       # keep trees off the mountain
                continue
            if random.random() > CFG["forest_keep"]:
                continue
            t = template.copy(); t.data = template.data; t.hide_render = False
            s = random.uniform(0.8, 1.35)
            t.scale = (s, s, s)
            t.rotation_euler = (0, 0, random.uniform(0, 6.28))
            t.location = (x, y, z - 0.2)
            coll.objects.link(t); placed += 1

    hx, hy = CFG["hero_at"]
    hero = make_conifer("HeroTree", 1.1, 9.0,
                        [(9.0, 6.5, 12.0), (14.0, 4.8, 10.0), (18.5, 3.0, 8.0)], bark, leaf)
    hero.location = (hx, hy, height(hx, hy) - 0.3)

    # --- 1.8 m human reference -----------------------------------------
    hmat = simple_material("HumanRef", (0.85, 0.10, 0.10, 1), roughness=0.6)
    mxp, myp = CFG["human_at"]; hp = []
    bpy.ops.mesh.primitive_cylinder_add(vertices=12, radius=0.22, depth=1.45, location=(mxp, myp, 0.725))
    body = bpy.context.active_object; body.data.materials.append(hmat); hp.append(body)
    bpy.ops.mesh.primitive_uv_sphere_add(radius=0.16, location=(mxp, myp, 1.62))
    head = bpy.context.active_object; head.data.materials.append(hmat); hp.append(head)
    bpy.ops.object.select_all(action="DESELECT")
    for o in hp:
        o.select_set(True)
    bpy.context.view_layer.objects.active = body
    bpy.ops.object.join()
    human = bpy.context.active_object; human.name = "Human_1m8"
    scene.cursor.location = (0, 0, 0); bpy.ops.object.origin_set(type="ORIGIN_CURSOR")
    human.location = (mxp, myp, height(mxp, myp))

    # --- sky / sun / camera --------------------------------------------
    world = bpy.data.worlds.get("MMOWorld") or bpy.data.worlds.new("MMOWorld")
    scene.world = world; world.use_nodes = True
    bg = world.node_tree.nodes.get("Background")
    if bg:
        bg.inputs[0].default_value = (0.50, 0.66, 0.92, 1); bg.inputs[1].default_value = 0.85
    sd = bpy.data.lights.new("Sun", type="SUN")
    sd.energy = 3.4; sd.angle = math.radians(2); sd.color = (1.0, 0.93, 0.82)
    sun = bpy.data.objects.new("Sun", sd); coll.objects.link(sun)
    sun.rotation_euler = (math.radians(60), math.radians(8), math.radians(-55))

    tgt = bpy.data.objects.new("CamTarget", None); coll.objects.link(tgt)
    tgt.location = (-3.0, 18.0, 10.0)
    cd = bpy.data.cameras.new("MMO_Cam"); cd.lens = 30; cd.clip_end = 2000
    cam = bpy.data.objects.new("MMO_Cam", cd); coll.objects.link(cam)
    cam.location = (26.0, -48.0, 13.0)
    trk = cam.constraints.new("TRACK_TO"); trk.target = tgt
    trk.track_axis = "TRACK_NEGATIVE_Z"; trk.up_axis = "UP_Y"
    scene.camera = cam

    # snap any open 3D viewport to the camera + material shading (no-op headless)
    screen = bpy.context.screen
    if screen:
        for area in screen.areas:
            if area.type == "VIEW_3D":
                for sp in area.spaces:
                    if sp.type == "VIEW_3D":
                        sp.region_3d.view_perspective = "CAMERA"
                        sp.shading.type = "MATERIAL"
                        sp.clip_end = 2000
    return {"trees": placed, "relief_m": round(zmax - zmin, 1),
            "hero_m": round(hero.dimensions.z, 1), "human_m": round(human.dimensions.z, 2)}


def render(path):
    scene = bpy.context.scene
    for eng in ("BLENDER_EEVEE_NEXT", "BLENDER_EEVEE", "CYCLES"):
        try:
            scene.render.engine = eng; break
        except Exception:
            continue
    scene.render.resolution_x = 1280
    scene.render.resolution_y = 720
    try:
        scene.eevee.taa_render_samples = 48
    except Exception:
        pass
    scene.render.filepath = path
    scene.render.image_settings.file_format = "PNG"
    bpy.ops.render.render(write_still=True)


if __name__ == "__main__":
    out = parse_args()
    info = build()
    print("[clearing] built:", info)
    if out:
        render(out)
        print("[clearing] wrote", out)
