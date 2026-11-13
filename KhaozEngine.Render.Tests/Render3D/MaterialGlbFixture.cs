using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using KhaozEngine.Imaging;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;
using SharpGLTF.Schema2;

namespace KhaozEngine.Tests.Render3D;

/// <summary>Textured glb fixtures for the material identity, residency and emissive tests: one UV-mapped triangle,
/// rigid or rigged, whose material the caller builds, saved either with its images embedded or with each image
/// written beside the glb under a name the caller chooses, so two glbs can reference one shared file.</summary>
internal static class MaterialGlbFixture
{
    /// <summary>A <paramref name="size"/> square PNG of one opaque colour.</summary>
    internal static byte[] Png(byte r, byte g, byte b, int size = 2)
    {
        var rgba = new byte[size * size * 4];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = r; rgba[i + 1] = g; rgba[i + 2] = b; rgba[i + 3] = 255;
        }
        return PngWriter.Encode(rgba, size, size);
    }

    /// <summary>A PNG whose colour no other call returns, so a test's images carry identities nothing else in the
    /// process loads. The loader shares decodes across the whole process while an image is referenced.</summary>
    internal static byte[] UniquePng(int size = 2)
    {
        byte[] seed = Guid.NewGuid().ToByteArray();
        return Png(seed[0], seed[1], seed[2], size);
    }

    /// <summary>Save a triangle drawn with <paramref name="material"/> to <paramref name="directory"/>. With
    /// <paramref name="satelliteNames"/> each logical image is written as its own file under the matching name,
    /// otherwise every image is embedded in the glb.</summary>
    internal static string Write(string directory, string fileName, MaterialBuilder material, bool rigged = false,
        IReadOnlyList<string>? satelliteNames = null)
    {
        ModelRoot model = rigged ? RiggedTriangle(material) : RigidTriangle(material);
        string path = Path.Combine(directory, fileName);
        if (satelliteNames is null)
        {
            model.SaveGLB(path);
            return path;
        }
        if (satelliteNames.Count != model.LogicalImages.Count)
            throw new ArgumentException("one satellite name per logical image", nameof(satelliteNames));
        for (int i = 0; i < satelliteNames.Count; i++) model.LogicalImages[i].AlternateWriteFileName = satelliteNames[i];
        model.SaveGLB(path, new WriteSettings { ImageWriting = ResourceWriteMode.SatelliteFile });
        return path;
    }

    static ModelRoot RigidTriangle(MaterialBuilder material)
    {
        var mesh = new MeshBuilder<VertexPositionNormal, VertexTexture1>("m");
        var prim = mesh.UsePrimitive(material);
        static VertexBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty> V(Vector3 p, Vector2 uv) =>
            new(new VertexPositionNormal(p, Vector3.UnitZ), new VertexTexture1(uv));
        prim.AddTriangle(V(new(0, 0, 0), new(0, 0)), V(new(1, 0, 0), new(1, 0)), V(new(0, 1, 0), new(0, 1)));
        var scene = new SceneBuilder();
        scene.AddRigidMesh(mesh, Matrix4x4.Identity);
        return scene.ToGltf2();
    }

    static ModelRoot RiggedTriangle(MaterialBuilder material)
    {
        var mesh = new MeshBuilder<VertexPositionNormal, VertexTexture1, VertexJoints4>("skin");
        var prim = mesh.UsePrimitive(material);
        static VertexBuilder<VertexPositionNormal, VertexTexture1, VertexJoints4> V(Vector3 p, Vector2 uv, int bone) =>
            new(new VertexPositionNormal(p, Vector3.UnitZ), new VertexTexture1(uv), new VertexJoints4((bone, 1f)));
        prim.AddTriangle(V(new(0, 0, 0), new(0, 0), 0), V(new(1, 0, 0), new(1, 0), 1), V(new(0, 1, 0), new(0, 1), 1));
        var bone0 = new NodeBuilder("bone0");
        var bone1 = bone0.CreateNode("bone1");
        bone1.LocalTransform = Matrix4x4.CreateTranslation(0, 1, 0);
        var scene = new SceneBuilder();
        scene.AddSkinnedMesh(mesh, Matrix4x4.Identity, bone0, bone1);
        return scene.ToGltf2();
    }
}
