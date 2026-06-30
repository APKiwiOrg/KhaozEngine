import bpy, sys, math
import bmesh
from mathutils import Vector
from collections import defaultdict

argv = sys.argv[sys.argv.index("--")+1:]
src = argv[0]

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=src)
meshes=[o for o in bpy.context.scene.objects if o.type=='MESH']

for o in meshes:
    me=o.data
    matnames=[m.name if m else f"slot{i}" for i,m in enumerate(me.materials)]
    bm=bmesh.new(); bm.from_mesh(me); bm.faces.ensure_lookup_table()
    # union-find over faces sharing an edge
    parent={}
    def find(x):
        while parent[x]!=x:
            parent[x]=parent[parent[x]]; x=parent[x]
        return x
    def union(a,b):
        ra,rb=find(a),find(b)
        if ra!=rb: parent[ra]=rb
    for f in bm.faces: parent[f.index]=f.index
    for e in bm.edges:
        lf=e.link_faces
        for i in range(1,len(lf)):
            union(lf[0].index, lf[i].index)
    comps=defaultdict(list)
    for f in bm.faces: comps[find(f.index)].append(f)
    print(f"=== {o.name}: {len(comps)} loose parts ===")
    parts=[]
    for root,faces in comps.items():
        mn=Vector((1e18,)*3); mx=Vector((-1e18,)*3)
        matcount=defaultdict(int)
        for f in faces:
            matcount[f.material_index]+=1
            for v in f.verts:
                w=o.matrix_world@v.co
                for i in range(3):
                    mn[i]=min(mn[i],w[i]); mx[i]=max(mx[i],w[i])
        dom=max(matcount, key=matcount.get)
        size=mx-mn
        vol=size.x*size.y*size.z
        parts.append((vol,len(faces),matnames[dom] if dom<len(matnames) else dom,mn,mx,size))
    parts.sort(reverse=True)
    for vol,nf,dom,mn,mx,size in parts:
        print(f"  vol={vol:6.2f} faces={nf:5d} mat={dom:14s} min=({mn.x:5.2f},{mn.y:5.2f},{mn.z:5.2f}) max=({mx.x:5.2f},{mx.y:5.2f},{mx.z:5.2f}) size=({size.x:4.2f},{size.y:4.2f},{size.z:4.2f})")
    bm.free()
