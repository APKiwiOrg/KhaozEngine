using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using KhaozEngine.Tests.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeRoofWeldOutlineTests
{
    [Fact]
    public void SelectedOutline_UsesSurvivingTrianglesAndCrossMemberWeldAverages()
    {
        // Source units are centimetres. Feed the authored bytes so proxy flattening sees the
        // same source triangles as the individual tier, including its original corner provenance.
        Vector3[] positions = [new(0, 0, 0), new(1, 0, 0), new(0, 20, 0), new(0, 0, 20), new(20, 0, 20), new(0, 20, 20)];
        var closure = NativeRenderAssetFixtures.Placements(coarsestLod: NativeProxyFixtures.Coarsest(positions));
        using var fixture = NativeSceneFixtures.SeparatedPlacements(closure, 1, new(8, 0, 8, 0, 1), new(8.04, 0, 8, 0, 1));
        fixture.Document.BuildingIntent = new(1, [], [], [], [])
        {
            Roofs = fixture.Document.Placements
            .Select(p => new MapPrefabReference(MapPrefabReferenceScope.World, MapPrefabChildKind.Placement, p.Id)).ToArray()
        };
        var scene = NativeSceneFixtures.Open(fixture, false).Acquire(NativePresentationFixtures.Scope).Scene!;
        using var assets = NativeMapPlacementTests.Assets(closure);
        var policy = new MapPlacementPolicy([new("caps", new HashSet<string> { "prop-a" }, [10, 20], 4, 1000, 30, 10, .1f, true)], 1000);
        var routing = MapPlacementRouting.Create(scene, policy, new HashSet<string>()).Routing!;
        using var batch = NativeMapPlacements.Prepare(scene, assets, policy, routing);
        var build = Assert.Single(batch.Clusters); Assert.Equal(2, build.MergedMesh.TriangleCount);
        batch.AcceptCluster(build.Request.Key, build, new MeshHandle(90));
        var sink = new MapDrawRecorder(); batch.Register(NativeMapPlacementTests.Stamp, sink);
        batch.RecordRoofOutline("sparse-0", NativeRoofHlodRecordingTests.Frame(), sink);
        var set = sink.Snapshot(); var outline = Assert.Single(set.Draws);
        var mesh = Assert.IsType<MapRigidResource>(Assert.Single(set.Resources, r => r.Reference == outline.Resource).Payload).Mesh;
        Assert.Equal(1, mesh.TriangleCount);
        var world = mesh.Vertices.Select(v => Vector3.Transform(v.Position, outline.LocalTransform) + new Vector3(outline.Anchor.X, outline.Anchor.Y, outline.Anchor.Z)).ToArray();
        Assert.Contains(world, p => System.MathF.Abs(p.X - 8.02f) < .00001f);
        Assert.DoesNotContain(world, p => p.Z < 8.1f);
        var colour = build.MergedMesh.Vertices.Select(v => v.Position + new Vector3(build.Request.Anchor.X, build.Request.Anchor.Y, build.Request.Anchor.Z)).ToArray();
        Assert.All(world, p => Assert.Contains(p, colour));
        Assert.Equal(build.Request.Anchor.X, outline.Anchor.X); Assert.Equal(Matrix4x4.Identity, outline.LocalTransform);
    }
}
