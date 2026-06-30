using System;
using System.IO;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public class GltfLoaderGroupsTests
{
    // Two separate box-ish meshes placed at two node transforms => two groups, each carrying its own
    // node-transformed geometry. (One triangle per object is enough to assert grouping + placement.)
    static string WriteTwoObjectGlb()
    {
        VertexBuilder<VertexPositionNormal, VertexEmpty, VertexEmpty> V(Vector3 p) =>
            new(new VertexPositionNormal(p, Vector3.UnitY));

        var a = new MeshBuilder<VertexPositionNormal, VertexEmpty, VertexEmpty>("a");
        a.UsePrimitive(MaterialBuilder.CreateDefault()).AddTriangle(
            V(new Vector3(0, 0, 0)), V(new Vector3(1, 0, 0)), V(new Vector3(0, 0, 1)));

        var b = new MeshBuilder<VertexPositionNormal, VertexEmpty, VertexEmpty>("b");
        b.UsePrimitive(MaterialBuilder.CreateDefault()).AddTriangle(
            V(new Vector3(0, 0, 0)), V(new Vector3(1, 0, 0)), V(new Vector3(0, 0, 1)));

        var scene = new SceneBuilder();
        scene.AddRigidMesh(a, Matrix4x4.Identity);
        scene.AddRigidMesh(b, Matrix4x4.CreateTranslation(10, 0, 0));

        string path = Path.Combine(Path.GetTempPath(), $"ke_groups_{System.Guid.NewGuid():N}.glb");
        scene.ToGltf2().SaveGLB(path);
        return path;
    }

    [Fact]
    public void LoadGroups_NoTriangles_Throws()
    {
        var scene = new SceneBuilder();   // empty: no meshes, no triangles
        string path = Path.Combine(Path.GetTempPath(), $"ke_groups_empty_{System.Guid.NewGuid():N}.glb");
        scene.ToGltf2().SaveGLB(path);
        try { Assert.Throws<InvalidOperationException>(() => GltfLoader.LoadGroups(path)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LoadGroups_ReturnsOneGroupPerObject_NodeTransformBaked()
    {
        string path = WriteTwoObjectGlb();
        try
        {
            var groups = GltfLoader.LoadGroups(path);
            Assert.Equal(2, groups.Count);
            // Each group is its own object; the second is translated +10 in X by its node transform.
            float maxX0 = groups[0].Vertices.Max(v => v.Position.X);
            float maxX1 = groups[1].Vertices.Max(v => v.Position.X);
            Assert.True(maxX0 < 5f, $"group 0 should sit near origin, maxX={maxX0}");
            Assert.True(maxX1 > 9.9f, $"group 1 should be translated to ~x=10, maxX={maxX1}");
        }
        finally { File.Delete(path); }
    }
}
