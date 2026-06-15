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

// Organic lumpiness for the asteroid: layered trig "noise" on the surface direction.
static float Lump(Vector3 p) =>
    0.16f * MathF.Sin(p.X * 4.1f) * MathF.Sin(p.Y * 3.7f + 1.3f) * MathF.Sin(p.Z * 4.3f + 2.1f)
  + 0.08f * MathF.Sin(p.X * 8.2f + 0.5f) * MathF.Cos(p.Z * 7.9f);

void Build(string path, Vector4 color, bool lumpy)
{
    var (vs, idx) = Ico(3);
    if (lumpy)
        for (int k = 0; k < vs.Count; k++)
            vs[k] = vs[k] * (1f + Lump(vs[k]));

    var mat = new MaterialBuilder("body").WithMetallicRoughnessShader()
        .WithBaseColor(color).WithMetallicRoughness(0f, 1f);
    var mesh = new MeshBuilder<VertexPosition, VertexEmpty, VertexEmpty>("ico");
    var prim = mesh.UsePrimitive(mat);
    for (int f = 0; f < idx.Count; f += 3)
    {
        Vector3 a = vs[idx[f]], b = vs[idx[f + 1]], c = vs[idx[f + 2]];
        // Ensure CCW winding as seen from outside so glTF-standard loaders get outward normals.
        if (Vector3.Dot(Vector3.Cross(b - a, c - a), a) < 0f) (b, c) = (c, b);
        prim.AddTriangle(new VertexPosition(a), new VertexPosition(b), new VertexPosition(c));
    }
    var scene = new SceneBuilder();
    scene.AddRigidMesh(mesh, Matrix4x4.Identity);
    string full = System.IO.Path.GetFullPath(path);
    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
    scene.ToGltf2().SaveGLB(full);
    Console.WriteLine($"wrote {full} verts={vs.Count} tris={idx.Count / 3}");
}

string dir = args.Length > 0 ? args[0] : "KhaozEngine.Render3D/assets";
Build(System.IO.Path.Combine(dir, "testmodel.glb"), new Vector4(0.85f, 0.55f, 0.30f, 1f), lumpy: false); // warm planet
Build(System.IO.Path.Combine(dir, "asteroid.glb"), new Vector4(0.46f, 0.44f, 0.42f, 1f), lumpy: true);    // gray asteroid
