import bpy, sys, os, math
from mathutils import Vector

argv = sys.argv[sys.argv.index("--")+1:]
src = argv[0]; outdir = argv[1]
drop = set(argv[2].split(",")) if len(argv) > 2 and argv[2] else set()
os.makedirs(outdir, exist_ok=True)

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=src)
meshes = [o for o in bpy.context.scene.objects if o.type == 'MESH']

# delete faces whose material name is in `drop`
import bmesh
for o in meshes:
    me = o.data
    drop_idx = {i for i,m in enumerate(me.materials) if m and m.name in drop}
    if not drop_idx: continue
    bm = bmesh.new(); bm.from_mesh(me)
    faces = [f for f in bm.faces if f.material_index in drop_idx]
    bmesh.ops.delete(bm, geom=faces, context='FACES')
    bm.to_mesh(me); bm.free()

def world_bounds(objs):
    mn = Vector((1e18,1e18,1e18)); mx = Vector((-1e18,-1e18,-1e18))
    for o in objs:
        for c in o.bound_box:
            w = o.matrix_world @ Vector(c)
            for i in range(3):
                mn[i]=min(mn[i],w[i]); mx[i]=max(mx[i],w[i])
    return mn,mx
mn,mx = world_bounds(meshes)
center=(mn+mx)*0.5
size=max(mx.x-mn.x, mx.y-mn.y, mx.z-mn.z)

scene=bpy.context.scene
scene.render.engine='BLENDER_WORKBENCH'
scene.display.shading.light='STUDIO'
scene.display.shading.color_type='MATERIAL'
scene.display.shading.show_xray=False
scene.render.resolution_x=1000; scene.render.resolution_y=1000
scene.render.film_transparent=False

cam_data=bpy.data.cameras.new("cam"); cam=bpy.data.objects.new("cam",cam_data)
scene.collection.objects.link(cam); scene.camera=cam
d=size*2.5

def render(name, loc, rot, ortho=None, persp=False):
    if persp:
        cam_data.type='PERSP'; cam_data.lens=50
    else:
        cam_data.type='ORTHO'; cam_data.ortho_scale=ortho
    cam.location=loc; cam.rotation_euler=rot
    scene.render.filepath=os.path.join(outdir,name)
    bpy.ops.render.render(write_still=True); print("RENDERED",name)

osc=size*1.25
# TOP looking down -Z (interior floor plan now roof is gone)
render("top_roofless.png",(center.x,center.y,mx.z+d),(0,0,0),ortho=osc)
# FRONT along +Y
render("front_roofless.png",(center.x,mn.y-d,center.z),(math.radians(90),0,0),ortho=osc)
# 3/4 perspective from front-left-above
import mathutils
eye=Vector((mn.x-size*0.9, mn.y-size*0.9, mx.z+size*0.6))
look=center
dirv=(look-eye).normalized()
# build rotation so -Z of camera points along dirv
quat=dirv.to_track_quat('-Z','Y')
cam_data.type='PERSP'; cam_data.lens=45
cam.location=eye; cam.rotation_euler=quat.to_euler()
scene.render.filepath=os.path.join(outdir,"persp.png")
bpy.ops.render.render(write_still=True); print("RENDERED persp.png")
print("DONE", f"center=({center.x:.2f},{center.y:.2f},{center.z:.2f}) size={size:.2f}")
