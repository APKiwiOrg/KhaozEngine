using System.IO;
using System.Numerics;
using KhaozEngine.Render3D;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Atlas readback pins the caster-face premise used by the underwater march acceptance scenes.</summary>
public sealed class UnderwaterCasterAtlasGpuTests(ITestOutputHelper output)
{
    [GpuFact]
    public void OpaquePrimitiveAndGltfStoreTheSameAboveWaterFace()
    {
        GltfMesh box = MeshPrimitives.Box();
        var mesh = new MeshBuilder<VertexPositionNormal, VertexEmpty, VertexEmpty>("opaque-box");
        var primitive = mesh.UsePrimitive(MaterialBuilder.CreateDefault());
        VertexBuilder<VertexPositionNormal, VertexEmpty, VertexEmpty> V(uint i) =>
            new(new VertexPositionNormal(box.Vertices[i].Position, box.Vertices[i].Normal));
        for (int i = 0; i < box.Indices32.Length; i += 3)
            primitive.AddTriangle(V(box.Indices32[i]), V(box.Indices32[i + 1]), V(box.Indices32[i + 2]));
        var source = new SceneBuilder();
        source.AddRigidMesh(mesh, Matrix4x4.Identity);
        using var stream = new MemoryStream();
        source.ToGltf2().WriteGLB(stream);
        GltfMesh loaded = GltfLoader.LoadFlattenedAlbedo(stream.ToArray(), "opaque-box");
        using var scenes = new UnderwaterSunlightScenes();
        Vector3 procedural = scenes.CaptureCasterFace(box), gltf = scenes.CaptureCasterFace(loaded);
        output.WriteLine($"Primitive stored point {procedural}, glTF stored point {gltf}. Hull top Y=0.15, bottom Y=-0.15.");
        Assert.InRange(procedural.Y, 0.149f, 0.151f);
        Assert.InRange(gltf.Y, 0.149f, 0.151f);
    }
}
