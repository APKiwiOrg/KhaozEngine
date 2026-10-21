using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using KhaozEngine.Tests.MapDoc.Scene;
using KhaozEngine.Tests.Render3D;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeMapPlacementTests
{
    internal static readonly MapFrameState Frame = new(WorldFrame.Origin, new(WorldFrame.Origin, Vector3.Zero),
        MapRoofMode.AlwaysVisible, 0, 0, new(WorldFrame.Origin, Vector3.Zero));
    internal static readonly MapResourceStamp Stamp = new("closure", "source", "scope", "policy", 1);
    internal static MapScopedScene Scene(MapAssetClosure closure)
    {
        using var fixture = NativeSceneFixtures.Placements(closure);
        var source = NativeSceneFixtures.Open(fixture, false);
        var scope = new MapSceneScope(new(WorldFrame.Origin, new(-16, -16), new(16, 16), null, null,
            Enum.GetValues<MapSurfaceRole>(), null, new()), new(-16, -16, -16, 16, 16, 16), MapSceneChannels.Placements | MapSceneChannels.Annotations);
        var result = source.Acquire(scope);
        Assert.True(result.Witness.CompleteForScope, string.Join("\n", result.Findings));
        return result.Scene!;
    }
    internal static MapRenderAssets Assets(MapAssetClosure closure)
    {
        string[] ids = { "prop-a", "prop-b", "cap-a" };
        var proofs = MapRenderExtentValidator.Validate(closure, ids, new(new()));
        Assert.Equal(MapRenderAssetStatus.Complete, proofs.Status);
        var result = MapRenderAssets.LoadSelected(closure, ids, proofs.Proofs!, new(new()));
        Assert.Equal(MapRenderAssetStatus.Complete, result.Status);
        return result.Assets!;
    }
    internal static MapPlacementPolicy Policy(bool cluster = false, float[]? distances = null) => new(
        new[] { new MapPlacementLayer("selected", new HashSet<string> { "prop-a" }, distances ?? new float[] { 10, 20 }, 4, 100,
            cluster ? 30 : 0, 10, 0, true) }, 100);
    static MapPlacementRouting Route(MapScopedScene scene, MapPlacementPolicy policy, params string[] animated) =>
        MapPlacementRouting.Create(scene, policy, new HashSet<string>(animated)).Routing!;
    static MapDrawSet Record(MapPlacementRenderBatch batch, MapFrameState? frame = null)
    {
        var recorder = new MapDrawRecorder(); batch.Register(Stamp, recorder); batch.Record(frame ?? Frame, recorder);
        return recorder.Snapshot();
    }

    [Fact]
    public void EffectivePlacementPartsAndEveryLod_TransformOnce()
    {
        var closure = NativeRenderAssetFixtures.Placements(); var scene = Scene(closure);
        using var assets = Assets(closure);
        var policy = Policy(); var routing = Route(scene, policy);
        using var batch = NativeMapPlacements.Prepare(scene, assets, policy, routing);
        Assert.Same(routing, batch.Routing);
        var placement = batch.Inventory.Single(p => p.PlacementId == "leaf-a");
        Assert.Equal(101, placement.NumericId); Assert.Equal("fixture-kind", placement.Kind);
        Assert.Equal("prop-a", placement.AssetId); Assert.Equal(new[] { "second", "first" }, placement.Tags);
        var set = Record(batch);
        var resources = set.Resources.Where(r => r.Reference.Key.OwnerId == "leaf-a").ToArray();
        Assert.Equal(new[] { 0, 1, 2 }, resources.Select(r => r.Reference.Key.Lod).Distinct());
        Assert.Equal(6, resources.Length);
        foreach (var resource in resources)
        {
            var mesh = Assert.IsType<MapRigidResource>(resource.Payload).Mesh;
            Assert.Equal(3, mesh.Vertices.Length);
            Assert.Equal(new[] { -.02f, -.049999997f, -.08f }[resource.Reference.Key.Lod], mesh.Vertices[0].Position.X);
            Assert.Equal(resource.Reference.Key.PartId == "part/0" ? .5f : .25f, Assert.IsType<MapRigidResource>(resource.Payload).Maps.AlphaCutoff);
            Assert.Equal(.04f, mesh.Vertices[0].Position.Z);
        }
        double yaw = (double).371f + .2f, scale = (double)1.2f * .75f;
        double x = -1.25 + (Math.Cos(.371f) * .125 + Math.Sin(.371f) * -.375) * 1.2f;
        double z = 3.75 + (-Math.Sin(.371f) * .125 + Math.Cos(.371f) * -.375) * 1.2f;
        var expected = new Matrix4x4((float)(Math.Cos(yaw) * scale), 0, (float)(-Math.Sin(yaw) * scale), 0,
            0, (float)scale, 0, 0, (float)(Math.Sin(yaw) * scale), 0, (float)(Math.Cos(yaw) * scale), 0,
            (float)(x - Math.Floor(x)), (float)(2.5 + .25 * 1.2f - 2), (float)(z - Math.Floor(z)), 1);
        var draws = set.Draws.Where(d => d.Resource.Key.OwnerId == "leaf-a").ToArray();
        Assert.Equal(2, draws.Length);
        Assert.All(draws, d => Assert.Equal(expected, d.LocalTransform));
        Assert.Single(draws.Select(d => d.Resource.Key.OwnerId).Distinct());
        Assert.All(resources, r => Assert.True(Math.Abs(r.Bounds.MinY - 2.6199) < .000001 && Math.Abs(r.Bounds.MaxY - 2.9801) < .000001));
    }

    [Fact]
    public void OrdinaryClusterAndAnimatedRoutes_AreMutuallyExclusive()
    {
        var closure = NativeRenderAssetFixtures.Placements(); var scene = Scene(closure);
        using var assets = Assets(closure);
        var policies = new[] { Policy(), Policy(), Policy(true) };
        var partitions = new List<MapPlacementRouting>();
        for (int i = 0; i < policies.Length; i++)
        {
            var routing = Route(scene, policies[i], i == 1 ? new[] { "prop-a" } : Array.Empty<string>());
            partitions.Add(routing);
            Assert.Equal(i == 1 ? MapPlacementRoute.Animated : i == 2 ? MapPlacementRoute.Cluster : MapPlacementRoute.Ordinary,
                routing.Entries["leaf-a"]);
            Assert.Equal(5, routing.Entries.Count);
            using var batch = NativeMapPlacements.Prepare(scene, assets, policies[i], routing);
            Assert.Equal(i == 2 ? 1 : 0, batch.Clusters.Count);
            Assert.Equal(i == 1 ? 0 : 2, Record(batch).Draws.Count(d => d.Resource.Key.OwnerId == "leaf-a"));
        }
        Assert.Equal(3, partitions.Select(p => p.PolicyDigest).Distinct().Count());
        var refused = MapPlacementRouting.Create(scene, Policy(true), new HashSet<string> { "prop-a" });
        Assert.Equal(MapSceneStatus.Invalid, refused.Status); Assert.Null(refused.Routing);
        var mixed = Route(scene, Policy(true), "prop-b");
        Assert.Equal(MapPlacementRoute.Cluster, mixed.Entries["leaf-a"]);
        Assert.Equal(MapPlacementRoute.Animated, mixed.Entries["leaf-c"]);
        Assert.Equal(MapPlacementRoute.Ordinary, mixed.Entries["roof-a"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RoofClusterPolicy_RefusesBeforeAnyRoutePrepares(bool sharedArchetype)
    {
        var closure = NativeRenderAssetFixtures.Placements(sharedArchetype); var scene = Scene(closure);
        var policy = sharedArchetype ? Policy(true) : new MapPlacementPolicy(new[]
        { new MapPlacementLayer("roof", new HashSet<string> { "cap-a" }, new float[] { 10, 20 }, 4, 100, 30, 10, 0, true) }, 100);
        var result = MapPlacementRouting.Create(scene, policy, new HashSet<string>());
        Assert.Equal(MapSceneStatus.Invalid, result.Status); Assert.Null(result.Routing);
    }

    [Fact]
    public void NativeClusterAdapter_RejectsRoofInjectedAfterRouting()
    {
        var closure = NativeRenderAssetFixtures.Placements(); var scene = Scene(closure);
        using var assets = Assets(closure); var policy = Policy(true); var routing = Route(scene, policy);
        using var batch = NativeMapPlacements.Prepare(scene, assets, policy, routing);
        var request = Assert.Single(batch.Clusters).Request;
        var roof = scene.Placements.Single(p => p.PlacementId == "roof-a");
        var injected = new NativePropInstance(roof.PlacementId, roof.NumericId, roof.AssetId, Matrix4x4.Identity, request.Anchor);
        var attack = new NativePropClusterRequest(request.Key, request.Generation, request.Area, request.Layer,
            request.Instances.Concat(new[] { injected }).ToArray(), request.Anchor);
        Assert.Throws<MapDocumentException>(() => NativeMapPlacements.PrepareCluster(scene, routing, attack));
        int lowerEntries = 0;
        Assert.Throws<MapDocumentException>(() => NativeMapPlacements.PrepareCluster(scene, routing, attack,
            input => { lowerEntries++; return PropClusterRenderer.BuildNativeCpu(input); }));
        Assert.Equal(0, lowerEntries);
    }

    [Fact]
    public void AcceptedNonRoofHlod_ContainsNoRoofTriangles()
    {
        var closure = NativeRenderAssetFixtures.Placements(); var scene = Scene(closure);
        using var assets = Assets(closure); var policy = Policy(true); var routing = Route(scene, policy, "prop-b");
        using var batch = NativeMapPlacements.Prepare(scene, assets, policy, routing);
        var build = Assert.Single(batch.Clusters);
        Assert.Equal(new[] { "leaf-a", "leaf-b" }, build.RepresentedIds);
        Assert.Equal(new[] { "room-a", "room-b" }, scene.BuildingIntent.RoofLinks.SelectMany(l => l.Rooms.Select(r => r.Id)).Order());
        Assert.Equal(4, build.MergedMesh.TriangleCount);
        Assert.All(build.MergedMesh.Vertices, v => Assert.True(v.Position.Y < 3));
        batch.AcceptCluster(build.Request.Key, build, new MeshHandle(90));
        var center = (build.Request.Area.MinX + build.Request.Area.MaxX) * .5f;
        var centerZ = (build.Request.Area.MinZ + build.Request.Area.MaxZ) * .5f;
        var frame = Frame with { Focus = new(WorldFrame.Origin, new(center + build.Request.Anchor.X + 30, 0, centerZ + build.Request.Anchor.Z)) };
        var set = Record(batch, frame);
        var hlod = set.Draws.Single(d => d.Resource.Key.PartId == "hlod");
        Assert.Equal(.5f, hlod.Rigid!.Value.Dissolve); Assert.True(hlod.Rigid.Value.InvertShadowDissolve);
        Assert.Equal(0, hlod.Rigid.Value.DissolveComplement);
        using var shared = PropClusterRenderer.CreateCpuOnly();
        shared.ApplyNative(build.Request.Key, build, new MeshHandle(90));
        var localFocus = new Vector3(center + 30, 0, centerZ);
        Assert.True(shared.TryGetNativeEmission(build.Request.Key, localFocus, out var coverage, out var emitted));
        Assert.True(coverage.DrawsMerged); Assert.Equal(new MeshHandle(90), emitted.Mesh);
        Assert.Equal(hlod.Rigid.Value, new MapRigidFrame(emitted.Tint, emitted.Material, emitted.Dissolve,
            emitted.DissolveEdgeWidth, emitted.DissolveEdgeColor, emitted.InvertShadowDissolve, emitted.DissolveComplement, emitted.Motion));
        var solid = PropClusterRenderer.MergedDraw(new MeshHandle(90), build.Request.Layer, 0, true);
        Assert.False(solid.InvertShadowDissolve);
        Assert.Equal(4, set.Draws.Count(d => d.Resource.Key.Kind == MapRenderKind.Roof));
        Assert.All(set.Draws.Where(d => d.Resource.Key.OwnerId is "leaf-a" or "leaf-b"), d => Assert.Equal(.5f, d.Rigid!.Value.Dissolve));
    }

    [Fact]
    public void NativeHlod_FallbackAndCrossfadeKeepOneIdentity()
    {
        var closure = NativeRenderAssetFixtures.Placements(); var scene = Scene(closure);
        using var assets = Assets(closure); var policy = Policy(true); var routing = Route(scene, policy);
        using var batch = NativeMapPlacements.Prepare(scene, assets, policy, routing);
        var before = Record(batch, Frame with { Focus = new(WorldFrame.Origin, new(50, 0, 0)) });
        Assert.DoesNotContain(before.Draws, d => d.Resource.Key.PartId == "hlod");
        Assert.Equal(new[] { "leaf-a", "leaf-b" }, Assert.Single(batch.Clusters).RepresentedIds);
        var lods = new Dictionary<string, IReadOnlyList<NativePropLod>>
        {
            ["a"] = new[]
        { new NativePropLod(0, new[] { new MeshHandle(1) }), new NativePropLod(1, new[] { new MeshHandle(2) }), new NativePropLod(2, new[] { new MeshHandle(3) }) }
        };
        var rows = new[] { new NativePropInstance("one", 17, "a", Matrix4x4.Identity, default) };
        var emitted = new List<RigidInstanceDraw>();
        Assert.Equal(1, PropRenderer.EmitNative(rows, lods, new(10, 0, 0), 100, new float[] { 10, 20 }, 4,
            emitted, static (list, _, draw) => list.Add(draw)));
        Assert.Equal(2, emitted.Count);
        Assert.Equal(new[] { 0f, 1f }, emitted.Select(d => d.DissolveComplement));
        Assert.All(emitted, d =>
        {
            Assert.Equal(.5f, d.Dissolve); Assert.False(d.InvertShadowDissolve);
            Assert.Equal(Color.White, d.Tint); Assert.Equal(Material.None, d.Material); Assert.Equal(MotionKey.None, d.Motion);
        });
    }

    [Fact]
    public void NativeHlod_EmptyProxyPreservesIndividuals()
    {
        var closure = NativeRenderAssetFixtures.Placements(); var scene = Scene(closure); using var assets = Assets(closure);
        var policy = new MapPlacementPolicy(new[] { new MapPlacementLayer("empty-proxy", new HashSet<string> { "prop-a" },
            new float[] { 10, 20 }, 4, 100, 30, 10, 1000, true) }, 100);
        var routing = Route(scene, policy);
        using var batch = NativeMapPlacements.Prepare(scene, assets, policy, routing);
        var build = Assert.Single(batch.Clusters);
        Assert.Equal(0, build.MergedMesh.TriangleCount);
        Assert.Throws<MapDocumentException>(() => batch.AcceptCluster(build.Request.Key, build, new MeshHandle(90)));
        var set = Record(batch, Frame with { Focus = new(WorldFrame.Origin, new(50, 0, 0)) });
        Assert.Equal(4, set.Draws.Count(d => d.Resource.Key.OwnerId is "leaf-a" or "leaf-b"));
        Assert.DoesNotContain(set.Draws, d => d.Resource.Key.PartId == "hlod");
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(15, 1)]
    [InlineData(25, 2)]
    public void EveryTier_IsSelectableAndEmptyThresholdsKeepBase(float distance, int expected)
    {
        var closure = NativeRenderAssetFixtures.Placements(); var scene = Scene(closure); using var assets = Assets(closure);
        var policy = Policy(); using var batch = NativeMapPlacements.Prepare(scene, assets, policy, Route(scene, policy));
        var pose = scene.Transforms["Placement:leaf-a"];
        var frame = Frame with { Focus = new(WorldFrame.Origin, new((float)pose.X + distance, 0, (float)pose.Z)) };
        var set = Record(batch, frame);
        Assert.All(set.Draws.Where(d => d.Resource.Key.OwnerId == "leaf-a"), d => Assert.Equal(expected, d.Resource.Key.Lod));
        var disabled = Policy(distances: Array.Empty<float>());
        using var baseBatch = NativeMapPlacements.Prepare(scene, assets, disabled, Route(scene, disabled));
        Assert.All(Record(baseBatch, frame).Draws.Where(d => d.Resource.Key.OwnerId == "leaf-a"), d => Assert.Equal(0, d.Resource.Key.Lod));
    }

    [Fact]
    public void SharedEmitterAndTypedRecorder_CarryEveryFrameFieldAtMidpoint()
    {
        var closure = NativeRenderAssetFixtures.Placements(); var scene = Scene(closure); using var assets = Assets(closure);
        var policy = Policy(); using var batch = NativeMapPlacements.Prepare(scene, assets, policy, Route(scene, policy));
        var pose = scene.Transforms["Placement:leaf-a"];
        var frame = Frame with { Focus = new(WorldFrame.Origin, new((float)pose.X + 10, 0, (float)pose.Z)) };
        var set = Record(batch, frame);
        var records = set.Draws.Where(d => d.Resource.Key.OwnerId == "leaf-a").ToArray();
        Assert.Equal(4, records.Length);
        Assert.All(records, r => Assert.InRange(r.Rigid!.Value.Dissolve, .499999f, .500001f));
        Assert.Equal(new[] { 0f, 0f, 1f, 1f }, records.Select(r => r.Rigid!.Value.DissolveComplement));
        var anchor = records[0].Anchor;
        var native = new[] { new NativePropInstance("leaf-a", 101, "prop-a", records[0].LocalTransform, new(anchor.X, anchor.Y, anchor.Z)) };
        var lods = new Dictionary<string, IReadOnlyList<NativePropLod>>
        {
            ["prop-a"] = new[]
        { new NativePropLod(0, new[] { new MeshHandle(1), new MeshHandle(2) }),
          new NativePropLod(1, new[] { new MeshHandle(3), new MeshHandle(4) }),
          new NativePropLod(2, new[] { new MeshHandle(5), new MeshHandle(6) }) }
        };
        using var harness = new MotionTestScene(); harness.Scene.Begin();
        PropRenderer.DrawNative(harness.Scene, native, lods, new Vector3((float)pose.X + 10, 0, (float)pose.Z), 100, policy.Layers[0].LodDistances, 4);
        var queued = harness.Scene.QueuedInstancesForTests;
        Assert.Equal(records.Length, queued.Count);
        for (int i = 0; i < records.Length; i++)
        {
            var actual = queued[i]; var expected = records[i].Rigid!.Value;
            Assert.Equal(expected, new MapRigidFrame(actual.Tint, actual.Material, actual.DissolveThreshold,
                actual.DissolveEdgeWidth, new Color(actual.DissolveEdge.X, actual.DissolveEdge.Y, actual.DissolveEdge.Z, actual.DissolveEdge.W), actual.InvertShadowDissolve, actual.DissolveComplement, actual.Motion));
            Assert.Equal(records[i].LocalTransform, actual.Anchored!.Value.Local);
        }
        Assert.Equal(MapEffectiveAnnotations.Markers(scene), batch.Markers);
        Assert.DoesNotContain(set.Draws, d => d.Resource.Key.Kind == MapRenderKind.Marker);
    }

    [Fact]
    public void UnknownPolicyAsset_RefusesBeforeAnyPreparation()
    {
        var scene = Scene(NativeRenderAssetFixtures.Placements());
        var layer = new MapPlacementLayer("unknown", new HashSet<string> { "absent" }, new float[] { 10, 20 }, 0, 100, 0, 0, 0, true);
        var result = MapPlacementRouting.Create(scene, new(new[] { layer }, 100), new HashSet<string>());
        Assert.Equal(MapSceneStatus.Invalid, result.Status); Assert.Null(result.Routing);
    }

    [Fact]
    public void PreparedInputLease_OutlivesItsCallerAndKeepsAllLodPayloads()
    {
        var closure = NativeRenderAssetFixtures.Placements(); var scene = Scene(closure);
        string[] ids = { "prop-a", "prop-b", "cap-a" };
        var proofs = MapRenderExtentValidator.Validate(closure, ids, new(new())).Proofs!;
        var budget = new GltfDecodeBudget(new());
        using var input = MapPreparedInputLease.LoadSelected(closure, ids, Array.Empty<string>(), proofs, budget).Input!;
        var policy = Policy(); var routing = Route(scene, policy);
        using var batch = NativeMapPlacements.Prepare(scene, input, policy, routing);
        long charge = batch.InputRetainedBytes;
        input.Dispose();
        Assert.True(charge > 0); Assert.Equal(charge, batch.InputRetainedBytes);
        Assert.Equal(30, Record(batch).Resources.Count);
    }

    [Fact]
    public void InvalidThresholdsAndProvenance_RefuseBeforePreparation()
    {
        var closure = NativeRenderAssetFixtures.Placements(); var scene = Scene(closure); using var assets = Assets(closure);
        Assert.Equal(MapSceneStatus.Invalid, MapPlacementRouting.Create(scene, Policy(distances: new float[] { 10 }), new HashSet<string>()).Status);
        var original = Policy(); var routing = Route(scene, original);
        Assert.Throws<MapDocumentException>(() => NativeMapPlacements.Prepare(scene, assets, Policy(true), routing));
        Assert.Throws<MapDocumentException>(() => NativeMapPlacements.Prepare(Scene(closure), assets, original, routing));
        var selected = new HashSet<string> { "prop-a" }; var distances = new float[] { 10, 20 };
        var layers = new[] { new MapPlacementLayer("frozen", selected, distances, 4, 100, 0, 0, 0, true) };
        var frozen = new MapPlacementPolicy(layers, 100); selected.Clear(); distances[0] = 99;
        Assert.Contains("prop-a", frozen.Layers[0].AssetIds); Assert.Equal(10, frozen.Layers[0].LodDistances[0]);
        Assert.Throws<ArgumentException>(() => Policy(distances: new float[] { 20, 10 }));
    }
}
