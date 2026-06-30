"""Build a collision-proxy GLB from a JSON box/wedge spec, in the source building's frame.
Usage: blender -b --python build_proxy.py -- <source.glb> <spec.json> <out_collision.glb> <overlay_dir>
Spec JSON: { "boxes":[{"name","min":[x,y,z],"max":[x,y,z]}...],
             "wedges":[{"name","min":[x,y,z],"max":[x,y,z],"axis":"x|y","dir":1|-1}...] }
A wedge is a right-triangular prism filling min..max, the sloped face rising along `axis` in `dir`
(a ramp for stairs). Coordinates are Blender (Z up), same frame as the imported source.
"""
import bpy, sys, os, json, math
from mathutils import Vector
import bmesh

argv = sys.argv[sys.argv.index("--")+1:]
src, spec_path, out_path, overlay_dir = argv[0], argv[1], argv[2], argv[3]
os.makedirs(overlay_dir, exist_ok=True)
spec = json.load(open(spec_path))

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=src)
src_objs = [o for o in bpy.context.scene.objects if o.type == 'MESH']
for o in src_objs:
    o.name = "SRC_" + o.name
    o.display_type = 'WIRE'

def add_box(name, mn, mx):
    cx,cy,cz = ((mn[0]+mx[0])/2,(mn[1]+mx[1])/2,(mn[2]+mx[2])/2)
    sx,sy,sz = (mx[0]-mn[0],mx[1]-mn[1],mx[2]-mn[2])
    me = bpy.data.meshes.new(name); bm = bmesh.new()
    bmesh.ops.create_cube(bm, size=1.0); bm.to_mesh(me); bm.free()
    o = bpy.data.objects.new(name, me)
    o.scale = (sx,sy,sz); o.location = (cx,cy,cz)
    bpy.context.scene.collection.objects.link(o)
    o.color = (0.9,0.1,0.1,1.0)
    return o

def add_wedge(name, mn, mx, axis, dr):
    # right-triangular prism: full height at one end along `axis`, zero at the other, extruded across the other horiz axis
    me = bpy.data.meshes.new(name); bm = bmesh.new()
    x0,y0,z0 = mn; x1,y1,z1 = mx
    if axis == 'x':
        # slope rises along x; cross-section in x-z, extruded along y
        if dr > 0:   # low at x0, high at x1
            prof = [(x0,z0),(x1,z0),(x1,z1)]
        else:        # high at x0
            prof = [(x0,z0),(x1,z0),(x0,z1)]
        verts = [bm.verts.new((px,y0,pz)) for (px,pz) in prof] + [bm.verts.new((px,y1,pz)) for (px,pz) in prof]
    else:
        if dr > 0:
            prof = [(y0,z0),(y1,z0),(y1,z1)]
        else:
            prof = [(y0,z0),(y1,z0),(y0,z1)]
        verts = [bm.verts.new((x0,py,pz)) for (py,pz) in prof] + [bm.verts.new((x1,py,pz)) for (py,pz) in prof]
    bm.faces.new(verts[0:3]); bm.faces.new(verts[3:6][::-1])
    bm.faces.new([verts[0],verts[1],verts[4],verts[3]])
    bm.faces.new([verts[1],verts[2],verts[5],verts[4]])
    bm.faces.new([verts[2],verts[0],verts[3],verts[5]])
    bm.normal_update(); bm.to_mesh(me); bm.free()
    o = bpy.data.objects.new(name, me); bpy.context.scene.collection.objects.link(o)
    o.color = (0.1,0.4,0.95,1.0)
    return o

proxy = []
for b in spec.get("boxes", []):
    proxy.append(add_box(b["name"], b["min"], b["max"]))
for w in spec.get("wedges", []):
    proxy.append(add_wedge(w["name"], w["min"], w["max"], w.get("axis","x"), w.get("dir",1)))

# ---- export proxy only (select proxy objects, export selected) ----
bpy.ops.object.select_all(action='DESELECT')
for o in proxy: o.select_set(True)
bpy.context.view_layer.objects.active = proxy[0] if proxy else None
bpy.ops.export_scene.gltf(filepath=out_path, use_selection=True, export_format='GLB',
                          export_yup=True, export_apply=True)
print("EXPORTED", out_path, "boxes/wedges=", len(proxy))

# ---- overlay renders: source as wireframe + proxy solid colored ----
def world_bounds(objs):
    mn=Vector((1e18,)*3); mx=Vector((-1e18,)*3)
    for o in objs:
        for c in o.bound_box:
            w=o.matrix_world@Vector(c)
            for i in range(3): mn[i]=min(mn[i],w[i]); mx[i]=max(mx[i],w[i])
    return mn,mx
mn,mx = world_bounds(src_objs); center=(mn+mx)*0.5; size=max(mx.x-mn.x,mx.y-mn.y,mx.z-mn.z)
scene=bpy.context.scene
scene.render.engine='BLENDER_WORKBENCH'
scene.display.shading.light='FLAT'
scene.display.shading.color_type='OBJECT'
scene.display.shading.show_object_outline=True
scene.display.shading.show_xray=True
scene.display.shading.xray_alpha=0.55
scene.render.resolution_x=1000; scene.render.resolution_y=1000
for o in src_objs: o.color=(0.25,0.55,0.95,1.0)
for o in src_objs: o.display_type='SOLID'
cam_data=bpy.data.cameras.new("cam"); cam=bpy.data.objects.new("cam",cam_data)
scene.collection.objects.link(cam); scene.camera=cam
d=size*2.5; osc=size*1.25
def orient(eye):
    dirv=(center-eye).normalized(); cam.location=eye; cam.rotation_euler=dirv.to_track_quat('-Z','Z').to_euler()
def render(name): scene.render.filepath=os.path.join(overlay_dir,name); bpy.ops.render.render(write_still=True); print("RENDERED",name)
cam_data.type='ORTHO'; cam_data.ortho_scale=osc
cam.location=(center.x,center.y,mx.z+d); cam.rotation_euler=(0,0,0); render("ov_top.png")
cam.location=(center.x,mn.y-d,center.z); cam.rotation_euler=(math.radians(90),0,0); render("ov_front.png")
cam.location=(mx.x+d,center.y,center.z); cam.rotation_euler=(math.radians(90),0,math.radians(90)); render("ov_right.png")
cam_data.type='PERSP'; cam_data.lens=45
orient(Vector((mn.x-size*0.8, mn.y-size*0.8, mx.z+size*0.5))); render("ov_persp.png")
print("DONE")
