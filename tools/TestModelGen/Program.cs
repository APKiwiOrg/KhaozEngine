using System;
using System.Collections.Generic;
using System.Numerics;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;

// One-off generator: emits a faceted low-poly icosphere .glb. Clear curvature for cel bands,
// clean silhouette for the edge outline. Run once; the output .glb is committed.

static (List<Vector3> v, List<int> i) Ico(int subdiv)
{
    float t = (1 + MathF.Sqrt(5)) / 2;
    var verts = new List<Vector3>
    {
        new(-1, t, 0), new(1, t, 0), new(-1, -t, 0), new(1, -t, 0),
        new(0, -1, t), new(0, 1, t), new(0, -1, -t), new(0, 1, -t),
        new(t, 0, -1), new(t, 0, 1), new(-t, 0, -1), new(-t, 0, 1),
    };
    var tris = new List<int>
    {
        0,11,5, 0,5,1, 0,1,7, 0,7,10, 0,10,11, 1,5,9, 5,11,4, 11,10,2, 10,7,6, 7,1,8,
        3,9,4, 3,4,2, 3,2,6, 3,6,8, 3,8,9, 4,9,5, 2,4,11, 6,2,10, 8,6,7, 9,8,1
    };
    for (int s = 0; s < subdiv; s++)
    {
        var nt = new List<int>();
        var mid = new Dictionary<long, int>();
        int Mid(int a, int b)
        {
            long key = (long)Math.Min(a, b) * 100000 + Math.Max(a, b);
            if (mid.TryGetValue(key, out var m)) return m;
            verts.Add(Vector3.Normalize((verts[a] + verts[b]) / 2));
            mid[key] = verts.Count - 1;
            return verts.Count - 1;
        }
        for (int f = 0; f < tris.Count; f += 3)
        {
            int a = tris[f], b = tris[f + 1], c = tris[f + 2];
            int ab = Mid(a, b), bc = Mid(b, c), ca = Mid(c, a);
            nt.AddRange(new[] { a, ab, ca, b, bc, ab, c, ca, bc, ab, bc, ca });
        }
        tris = nt;
    }
    for (int k = 0; k < verts.Count; k++) verts[k] = Vector3.Normalize(verts[k]);
    return (verts, tris);
}

var (vs, idx) = Ico(2);
var mat = new MaterialBuilder("body")
    .WithMetallicRoughnessShader()
    .WithBaseColor(new Vector4(0.85f, 0.55f, 0.30f, 1f))
    .WithMetallicRoughness(0f, 1f);

var mesh = new MeshBuilder<VertexPosition, VertexEmpty, VertexEmpty>("ico");
var prim = mesh.UsePrimitive(mat);
for (int f = 0; f < idx.Count; f += 3)
{
    prim.AddTriangle(
        new VertexPosition(vs[idx[f]]),
        new VertexPosition(vs[idx[f + 1]]),
        new VertexPosition(vs[idx[f + 2]]));
}

var scene = new SceneBuilder();
scene.AddRigidMesh(mesh, Matrix4x4.Identity);
var model = scene.ToGltf2();

string outPath = args.Length > 0 ? args[0] : "KhaozEngine.Render3D/assets/testmodel.glb";
outPath = System.IO.Path.GetFullPath(outPath);
System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(outPath)!);
model.SaveGLB(outPath);
Console.WriteLine($"wrote {outPath} verts={vs.Count} tris={idx.Count / 3}");
