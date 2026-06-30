import bpy, sys, os, math
from mathutils import Vector

# args after '--'
argv = sys.argv[sys.argv.index("--")+1:]
src = argv[0]
outdir = argv[1]
os.makedirs(outdir, exist_ok=True)

# clean scene
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=src)

# gather mesh objects
meshes = [o for o in bpy.context.scene.objects if o.type == 'MESH']
print("MESH OBJECTS:", [o.name for o in meshes])

def world_bounds(objs):
    mn = Vector((1e18,1e18,1e18)); mx = Vector((-1e18,-1e18,-1e18))
    for o in objs:
        for c in o.bound_box:
            w = o.matrix_world @ Vector(c)
            for i in range(3):
                mn[i] = min(mn[i], w[i]); mx[i] = max(mx[i], w[i])
    return mn, mx

mn, mx = world_bounds(meshes)
print(f"OVERALL_BOUNDS_BLENDER min=({mn.x:.3f},{mn.y:.3f},{mn.z:.3f}) max=({mx.x:.3f},{mx.y:.3f},{mx.z:.3f})")
print(f"OVERALL_SIZE (x={mx.x-mn.x:.3f}, y={mx.y-mn.y:.3f}, z={mx.z-mn.z:.3f})  [Blender Z is up]")

# per-material bounds (group polygons by material slot)
for o in meshes:
    me = o.data
    mats = me.materials
    # init per-slot accumulators
    acc = {}
    for p in me.polygons:
        si = p.material_index
        for vi in p.vertices:
            wv = o.matrix_world @ me.vertices[vi].co
            if si not in acc:
                acc[si] = [Vector((1e18,1e18,1e18)), Vector((-1e18,-1e18,-1e18)), 0]
            a = acc[si]
            for i in range(3):
                a[0][i] = min(a[0][i], wv[i]); a[1][i] = max(a[1][i], wv[i])
            a[2]+=1
    print(f"--- object {o.name}: {len(mats)} material slots ---")
    for si in sorted(acc.keys()):
        name = mats[si].name if si < len(mats) and mats[si] else f"slot{si}"
        a = acc[si]
        bmn, bmx = a[0], a[1]
        print(f"  MAT[{si}] {name:14s} bounds min=({bmn.x:.2f},{bmn.y:.2f},{bmn.z:.2f}) max=({bmx.x:.2f},{bmx.y:.2f},{bmx.z:.2f}) size=({bmx.x-bmn.x:.2f},{bmx.y-bmn.y:.2f},{bmx.z-bmn.z:.2f}) verts={a[2]}")

# ---- render 3 orthographic views with Workbench, material colors ----
scene = bpy.context.scene
scene.render.engine = 'BLENDER_WORKBENCH'
scene.display.shading.light = 'STUDIO'
scene.display.shading.color_type = 'MATERIAL'
scene.render.resolution_x = 900
scene.render.resolution_y = 900
scene.render.film_transparent = False

center = (mn + mx) * 0.5
size = max(mx.x-mn.x, mx.y-mn.y, mx.z-mn.z)
ortho_scale = size * 1.25

# camera
cam_data = bpy.data.cameras.new("cam")
cam_data.type = 'ORTHO'
cam_data.ortho_scale = ortho_scale
cam = bpy.data.objects.new("cam", cam_data)
scene.collection.objects.link(cam)
scene.camera = cam

def render_view(name, loc, rot):
    cam.location = loc
    cam.rotation_euler = rot
    scene.render.filepath = os.path.join(outdir, name)
    bpy.ops.render.render(write_still=True)
    print("RENDERED", name)

d = size * 2.5
# TOP: look down -Z (camera above, looking down). rot x=0 -> looks down -Z by default
render_view("top.png",  (center.x, center.y, mx.z + d), (0, 0, 0))
# FRONT: look along +Y (camera at -Y, looking toward +Y). cam looks down -Z by default; rotate x=90deg to look along +Y
render_view("front.png",(center.x, mn.y - d, center.z), (math.radians(90), 0, 0))
# RIGHT: look along -X (camera at +X). rotate x=90, z=90
render_view("right.png",(mx.x + d, center.y, center.z), (math.radians(90), 0, math.radians(90)))
print("DONE")
