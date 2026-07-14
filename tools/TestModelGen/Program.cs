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

// --- Multi-material textured prop: a two-material signpost -----------------------------------------
// A wooden post (material A, vertical wood-grain texture) plus a sign board on top (material B, a
// distinct painted-checker texture). Two primitives, two materials, each with its own baseColor
// texture, so the engine's multi-texture-per-primitive path (GltfLoader.LoadPartsWithMaterials /
// PropLoader.LoadPropParts / Scene3D.LoadProp) renders two distinct textures on two sub-ranges. The
// asset is 100% procedurally generated here (fully original), so it is CC0 / public domain.
static byte[] WoodTexture(int size)
{
    var px = new byte[size * size * 4];
    for (int y = 0; y < size; y++)
    for (int x = 0; x < size; x++)
    {
        // Vertical grain: brown base modulated by a few sine bands down the U axis, plus faint noise.
        float u = (float)x / size;
        float grain = 0.5f + 0.5f * MathF.Sin(u * MathF.PI * 14f);
        float knot = 0.5f + 0.5f * MathF.Sin(u * MathF.PI * 3f + 1.3f);
        float v = 0.55f + 0.30f * grain * knot;
        int i = (y * size + x) * 4;
        px[i + 0] = Byte(0.42f * v + 0.12f);   // warm brown
        px[i + 1] = Byte(0.26f * v + 0.06f);
        px[i + 2] = Byte(0.12f * v + 0.02f);
        px[i + 3] = 255;
    }
    return KhaozEngine.Imaging.PngWriter.Encode(px, size, size);
}
static byte[] CheckerTexture(int size)
{
    var px = new byte[size * size * 4];
    int cell = size / 8;
    for (int y = 0; y < size; y++)
    for (int x = 0; x < size; x++)
    {
        bool on = ((x / cell) + (y / cell)) % 2 == 0;
        int i = (y * size + x) * 4;
        // Teal vs cream, unmistakably different from the brown wood and clearly a repeating pattern.
        px[i + 0] = on ? (byte)0xF2 : (byte)0x18;
        px[i + 1] = on ? (byte)0xE8 : (byte)0x9C;
        px[i + 2] = on ? (byte)0xC8 : (byte)0x8E;
        px[i + 3] = 255;
    }
    return KhaozEngine.Imaging.PngWriter.Encode(px, size, size);
}
static byte Byte(float f) => (byte)Math.Clamp((int)(f * 255f + 0.5f), 0, 255);

// Add an axis-aligned box (center, half-extents) to a primitive with outward normals + per-face [0,1] UVs.
static void AddBox(PrimitiveBuilder<MaterialBuilder, VertexPositionNormal, VertexTexture1, VertexEmpty> prim,
                   Vector3 c, Vector3 h)
{
    // 6 faces; each as two triangles. n = outward normal; corners wound CCW as seen from outside.
    void Face(Vector3 n, Vector3 uAxis, Vector3 vAxis)
    {
        Vector3 origin = c + Mul(n, h) - uAxis - vAxis;
        Vector3 p00 = origin, p10 = origin + uAxis * 2f, p11 = origin + uAxis * 2f + vAxis * 2f, p01 = origin + vAxis * 2f;
        VertexBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty> V(Vector3 p, float uu, float vv) =>
            new(new VertexPositionNormal(p, n), new VertexTexture1(new Vector2(uu, vv)));
        prim.AddTriangle(V(p00, 0, 0), V(p10, 1, 0), V(p11, 1, 1));
        prim.AddTriangle(V(p00, 0, 0), V(p11, 1, 1), V(p01, 0, 1));
    }
    Vector3 Mul(Vector3 a, Vector3 b) => new(a.X * b.X, a.Y * b.Y, a.Z * b.Z);
    Face(new Vector3(0, 0, 1), new Vector3(h.X, 0, 0), new Vector3(0, h.Y, 0));   // +Z
    Face(new Vector3(0, 0, -1), new Vector3(-h.X, 0, 0), new Vector3(0, h.Y, 0)); // -Z
    Face(new Vector3(1, 0, 0), new Vector3(0, 0, -h.Z), new Vector3(0, h.Y, 0));  // +X
    Face(new Vector3(-1, 0, 0), new Vector3(0, 0, h.Z), new Vector3(0, h.Y, 0));  // -X
    Face(new Vector3(0, 1, 0), new Vector3(h.X, 0, 0), new Vector3(0, 0, -h.Z));  // +Y
    Face(new Vector3(0, -1, 0), new Vector3(h.X, 0, 0), new Vector3(0, 0, h.Z));  // -Y
}

static void BuildSignpost(string path)
{
    var postMat = new MaterialBuilder("post").WithMetallicRoughnessShader()
        .WithBaseColor(new SharpGLTF.Memory.MemoryImage(WoodTexture(64))).WithMetallicRoughness(0f, 0.85f);
    var signMat = new MaterialBuilder("sign").WithMetallicRoughnessShader()
        .WithBaseColor(new SharpGLTF.Memory.MemoryImage(CheckerTexture(64))).WithMetallicRoughness(0f, 0.6f);

    var mesh = new MeshBuilder<VertexPositionNormal, VertexTexture1>("signpost");
    AddBox(mesh.UsePrimitive(postMat), new Vector3(0f, 0.8f, 0f), new Vector3(0.08f, 0.8f, 0.08f));   // post
    AddBox(mesh.UsePrimitive(signMat), new Vector3(0f, 1.45f, 0f), new Vector3(0.45f, 0.28f, 0.04f)); // sign board

    var scene = new SceneBuilder();
    scene.AddRigidMesh(mesh, Matrix4x4.Identity);
    string full = System.IO.Path.GetFullPath(path);
    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
    scene.ToGltf2().SaveGLB(full);
    Console.WriteLine($"wrote {full} (2-material textured signpost)");
}

if (args.Length > 0 && args[0] == "signpost")
{
    BuildSignpost(args.Length > 1 ? args[1] : "KhaozEngine.Showcase/assets/props/signpost.glb");
    return;
}

string dir = args.Length > 0 ? args[0] : "KhaozEngine.Render3D/assets";
Build(System.IO.Path.Combine(dir, "testmodel.glb"), new Vector4(0.85f, 0.55f, 0.30f, 1f), lumpy: false); // warm planet
Build(System.IO.Path.Combine(dir, "asteroid.glb"), new Vector4(0.46f, 0.44f, 0.42f, 1f), lumpy: true);    // gray asteroid
