using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.MapDocOracle;

[UnsupportedOSPlatform("windows")]
public sealed class SourceProxyFrameTests
{
    [Fact]
    public void NonWeldedProxy_RoundsSourceOnceInClusterFrame()
    {
        var draw = Capture(0);
        // 512.5 + (0.5 - 2^-16) rounds to 513 in binary32 absolute world space.
        // Subtracting anchor 512 first retains 1 - 2^-16, a 15.2587890625 micrometre gap.
        Assert.Equal(.9999847412109375f, draw.Mesh.Vertices[0].Position.X);
        Assert.Equal(1.52587890625e-5, 1d - draw.Mesh.Vertices[0].Position.X);
    }

    [Fact]
    public void NonWeldedProxy_RejectsRealTenthMillimetreChange()
    {
        var draw = Capture(0); var vertices = draw.Mesh.Vertices.ToArray();
        vertices[0].Position.X += .0001f;
        var changed = new GltfMesh(vertices, draw.Mesh.Indices32.ToArray());
        var frame = Frame(draw);
        Assert.False(SceneProxyGeometry.Equal(draw.Mesh, frame, changed, frame));
    }

    [Fact]
    public void WeldedProxy_KeepsBinary32LegacyReplay()
    {
        var draw = Capture(.01f);
        var mesh = PropHlod.BuildMergedMesh([Placement], Meshes(), .01f);
        var vertices = mesh.Vertices.ToArray();
        for (int i = 0; i < vertices.Length; i++) vertices[i].Position -= new Vector3(512, 0, -64);
        Assert.Equal(vertices, draw.Mesh.Vertices);
        Assert.Equal(mesh.Indices32, draw.Mesh.Indices32);
    }

    static readonly PropPlacement Placement = new("source", 512.5f, 0, -32, 1, 0, 0);
    static Dictionary<string, GltfMesh> Meshes() => new()
    {
        ["source"] = new(new[]
        {
            new ModelVertex(new(.4999847412109375f, 0, 0), Vector3.UnitZ, Vector4.One),
            new ModelVertex(new(1.4999847412109375f, 0, 0), Vector3.UnitZ, Vector4.One),
            new ModelVertex(new(.4999847412109375f, 1, 0), Vector3.UnitZ, Vector4.One)
        }, new uint[] { 0, 1, 2 })
    };
    static LegacySceneDraw Capture(float weld)
    {
        var scene = new LegacyRecordingScene
        {
            ClusterAnchor = new(512, 0, -64),
            ClusterKey = new(MapRenderKind.Placement, "cluster/source/8/-1/0", "hlod", 0)
        };
        var meshes = Meshes();
        var handles = new Dictionary<string, IReadOnlyList<MeshHandle>> { ["source"] = new[] { scene.LoadMesh(meshes["source"]) } };
        var layer = PropLayer.PlacementLayer([Placement], handles, 100).WithHlod(meshes, 10, weld, 4);
        var key = new PropClusterKey("source", 8, -1, 0);
        using var cluster = new LegacyClusterOwner(scene);
        var build = cluster.BuildCpu(new(key, 1, new(512, -64, 576, 0), layer, [Placement]));
        Assert.True(build.Succeeded);
        cluster.Apply(key, build); cluster.Draw(new(590, 0, -32));
        return Assert.Single(scene.Draws);
    }
    static MapDrawRecord Frame(LegacySceneDraw draw) => new(new(draw.Key!.Value, new("s", "s", "s", "s", 0)),
        MapDrawChannels.Colour, new MapSubmissionAnchor(512, 0, -64), Matrix4x4.Identity, Vector3.Zero, null, null, null, null);
}
