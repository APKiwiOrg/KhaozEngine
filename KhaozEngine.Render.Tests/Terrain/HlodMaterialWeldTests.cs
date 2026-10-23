using System;
using System.IO;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.TileWorld;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;
using Xunit;

namespace KhaozEngine.Tests.Terrain;

public sealed class HlodMaterialWeldTests
{
    [Theory]
    [InlineData(0f)]
    [InlineData(.0001f)]
    [InlineData(.5f)]
    public void Flat_Hlod_mesh_preserves_coincident_corners_from_distinct_materials(float difference)
    {
        Vector4 first = new(.8f, .2f, .1f, 1f);
        Vector4 second = first + new Vector4(0f, difference, 0f, 0f);
        var mesh = new MeshBuilder<VertexPositionNormal, VertexEmpty>("material-seam");
        var a = mesh.UsePrimitive(new MaterialBuilder("a").WithBaseColor(first));
        var b = mesh.UsePrimitive(new MaterialBuilder("b").WithBaseColor(second));
        VertexBuilder<VertexPositionNormal, VertexEmpty, VertexEmpty> V(float x, float y) =>
            new(new VertexPositionNormal(new Vector3(x, y, 0f), Vector3.UnitZ));
        a.AddTriangle(V(0f, 0f), V(1f, 0f), V(0f, 1f));
        b.AddTriangle(V(1f, 0f), V(1f, 1f), V(0f, 1f));
        var scene = new SceneBuilder();
        scene.AddRigidMesh(mesh, Matrix4x4.Identity);
        string directory = Path.Combine(Path.GetTempPath(), "khaozengine-hlod-weld-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "seam.glb");
            scene.ToGltf2().SaveGLB(path);
            var resolver = new GltfMeshResolver(directory);
            GltfMesh flat = Assert.IsType<GltfMesh>(resolver.ResolveFlatForHlod(
                new TileObjectArchetype { Id = "seam", Name = "seam", MeshRef = "seam.glb" }));
            Assert.Equal(difference == 0f ? 4 : 6, flat.Vertices.Length);
            Assert.Equal(6, flat.Indices32.Length);
            Assert.All(flat.Indices32.Take(3), index => Assert.Equal(first, flat.Vertices[index].Color));
            Assert.All(flat.Indices32.Skip(3), index => Assert.Equal(second, flat.Vertices[index].Color));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
