using System;
using System.Numerics;
using System.Linq;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

[Collection("AllocSensitive")]
public sealed class NativeMapPickTests
{
    [Fact]
    public void HiddenFirstHit_ContinuesPerChannel()
    {
        using var fixture = NativePresentationFixtures.Create(); using var plan = NativePresentationFixtures.Plan(fixture);
        var picker = new MapPresentationPicker(plan); var ray = new MapPresentationRay(new(WorldFrame.Origin, new(0, .5f, 0)), Vector3.UnitZ, 10);
        foreach (var (channel, owner, distance) in new[] { (MapPresentationChannel.Selection, "object-behind", 5d),
            (MapPresentationChannel.Camera, "object-behind", 5d), (MapPresentationChannel.Sky, "roof-a", 2d), (MapPresentationChannel.Physical, "roof-a", 2d) })
        {
            var hit = picker.Pick(ray, channel, NativePresentationFixtures.Frame());
            Assert.NotNull(hit); Assert.Equal(owner, hit.Value.OwnerId); Assert.Equal(distance, hit.Value.Distance);
            Assert.Equal(-Vector3.UnitZ, hit.Value.Normal); Assert.Equal(new(0, .5f, (float)distance), hit.Value.LocalPoint);
        }
        Assert.Equal("roof-a", picker.Pick(ray, MapPresentationChannel.Selection,
            NativePresentationFixtures.Frame(mode: MapRoofMode.AlwaysVisible))!.Value.OwnerId);
    }

    [Fact]
    public void IdentityCut_DefaultAndRejectedFrontHitContinue()
    {
        using var fixture = NativePresentationFixtures.Create(); using var plan = NativePresentationFixtures.Plan(fixture);
        var ray = new MapPresentationRay(new(WorldFrame.Origin, new(0, .5f, 0)), Vector3.UnitZ, 10);
        var frame = NativePresentationFixtures.Frame(mode: MapRoofMode.AlwaysVisible);
        Assert.Equal("roof-a", new MapPresentationPicker(plan).Pick(ray, MapPresentationChannel.Selection, frame)!.Value.OwnerId);
        Assert.Equal("object-behind", new MapPresentationPicker(plan, new RejectRoof()).Pick(ray, MapPresentationChannel.Selection, frame)!.Value.OwnerId);
    }

    [Fact]
    public void SelectionRaisesLowEnvelopeAndPrefersExplicitShapeWithoutInflatingCollider()
    {
        using var fixture = NativePresentationFixtures.Create(); using var plan = NativePresentationFixtures.Plan(fixture);
        var picker = new MapPresentationPicker(plan); var frame = NativePresentationFixtures.Frame();
        var low = new MapPresentationRay(new(WorldFrame.Origin, new(3, .8f, 0)), Vector3.UnitZ, 10);
        Assert.Equal("low", picker.Pick(low, MapPresentationChannel.Selection, frame)!.Value.OwnerId);
        Assert.Null(picker.Pick(low, MapPresentationChannel.Physical, frame));
        var edge = new MapPresentationRay(new(WorldFrame.Origin, new(5.3f, .8f, 0)), Vector3.UnitZ, 10);
        Assert.Null(picker.Pick(edge, MapPresentationChannel.Selection, frame));
        Assert.Equal("explicit", picker.Pick(edge, MapPresentationChannel.Physical, frame)!.Value.OwnerId);
    }
    [Fact]
    public void MovingObserverModesLodAndHiddenContinuation_AllocateNothingAfterWarmup()
    {
        using var fixture = NativePresentationFixtures.Create(); using var plan = NativePresentationFixtures.Plan(fixture);
        var picker = new MapPresentationPicker(plan, new RejectRoof()); var sink = new CountSink(); plan.Register(sink);
        var ray = new MapPresentationRay(new(WorldFrame.Origin, new(0, .5f, 0)), Vector3.UnitZ, 10);
        void Exercise()
        {
            var frame = NativePresentationFixtures.Frame(); plan.Update(frame); plan.Record(frame, sink);
            _ = picker.Pick(ray, MapPresentationChannel.Selection, frame);
            _ = picker.Pick(new(new(WorldFrame.Origin, new(3, .8f, 0)), Vector3.UnitZ, 10), MapPresentationChannel.Selection, frame);
            frame = NativePresentationFixtures.Frame(9); plan.Record(frame, sink); _ = picker.Pick(ray, MapPresentationChannel.Camera, frame);
            frame = NativePresentationFixtures.Frame(1, MapRoofMode.AlwaysHidden); plan.Record(frame, sink);
            _ = picker.Pick(ray, MapPresentationChannel.Physical, frame);
            frame = NativePresentationFixtures.Frame(1, MapRoofMode.AlwaysVisible); plan.Record(frame, sink);
            _ = picker.Pick(ray, MapPresentationChannel.Sky, frame);
        }
        Exercise(); Exercise();
        long total = AllocationMeasurement.Measure("native roof motion and picks", Exercise);
        long update = AllocationMeasurement.Measure("update", () => plan.Update(NativePresentationFixtures.Frame()));
        long record = AllocationMeasurement.Measure("record", () => plan.Record(NativePresentationFixtures.Frame(), sink));
        long pick = AllocationMeasurement.Measure("pick", () => picker.Pick(ray, MapPresentationChannel.Selection, NativePresentationFixtures.Frame()));
        Assert.True(total == 0, $"total={total}, update={update}, record={record}, pick={pick}");
        Assert.True(sink.Count > 0);
    }
    [Fact]
    public void EqualDistanceUsesOrdinalOwnerAndInsideHitStartsAtZero()
    {
        using var fixture = NativePresentationFixtures.Create();
        fixture.Document.Placements.Add(new() { Id = "aaa", AssetId = "object", Kind = "invented", X = 0, Y = .5f, Z = 5.5f, NumericId = 7 });
        fixture.Document.NumericIdHighWaterMark = 7;
        using var plan = NativePresentationFixtures.Plan(fixture); var picker = new MapPresentationPicker(plan);
        var frame = NativePresentationFixtures.Frame();
        var ray = new MapPresentationRay(new(WorldFrame.Origin, new(0, .5f, 0)), Vector3.UnitZ * 2, 10);
        var tie = picker.Pick(ray, MapPresentationChannel.Selection, frame)!.Value;
        Assert.Equal("aaa", tie.OwnerId); Assert.Equal(7, tie.NumericId); Assert.Equal(5, tie.Distance);
        var inside = picker.Pick(ray with { Origin = new(WorldFrame.Origin, new(0, .5f, 5.5f)) }, MapPresentationChannel.Physical, frame)!.Value;
        Assert.Equal("aaa", inside.OwnerId); Assert.Equal(0, inside.Distance); Assert.Equal(-Vector3.UnitZ, inside.Normal);
        Assert.Null(picker.Pick(ray with { MaxDistance = 1.99 }, MapPresentationChannel.Physical, frame));
        Assert.Throws<ArgumentOutOfRangeException>(() => picker.Pick(ray with { Direction = Vector3.Zero }, MapPresentationChannel.Selection, frame));
    }

    [Fact]
    public void FramedRayPreservesPrecisePlacementPoseAndReturnsRequestedLocalPoint()
    {
        using var fixture = NativePresentationFixtures.Create();
        var placement = fixture.Document.Placements.Single(p => p.Id == "object-behind");
        fixture.Document.Placements = [placement]; fixture.Document.BuildingIntent = null; fixture.Document.Surfaces = new();
        placement.X = 32000; placement.Y = 1; placement.Z = 32005.5f;
        fixture.Document.NativeResourceTransforms = [new(MapPrefabChildKind.Placement, placement.Id,
            new(32000.0005, 1, 32005.5, 0, 1), new string('a', 64))];
        var scope = NativePresentationFixtures.Scope with
        {
            Surfaces = NativePresentationFixtures.Scope.Surfaces with
            { Frame = new(250, 250), LocalMin = new(-4, -4), LocalMax = new(8, 8) },
            DrawBounds = new(31996, -500, 31996, 32008, 500, 32008)
        };
        var state = fixture.State(scope); var scene = state.Source.Acquire(scope).Scene!;
        using var cache = new MapRenderAssetCache();
        var input = cache.Acquire(state.Closure, ["object"], [], state.Extents, new(new())).Input!;
        using var plan = MapRenderPlan.Prepare(state.Source, scene, input, state.Policy, 1);
        var picker = new MapPresentationPicker(plan);
        var frame = NativePresentationFixtures.Frame() with { Frame = new(250, 250), Observer = new(new(250, 250), new(0, 1, 0)) };
        var ray = new MapPresentationRay(new(new(250, 250), new(2, 1, 5.5f)), -Vector3.UnitX, 10);
        var hit = picker.Pick(ray, MapPresentationChannel.Physical, frame)!.Value;
        Assert.Equal(1.4995, hit.Distance, 7); Assert.Equal(.5005f, hit.LocalPoint.X, 6); Assert.Equal(Vector3.UnitX, hit.Normal);
    }

    [Fact]
    public void CanonicalFloorAndUnlinkedCeilingRemainPhysicalAndSkyGeometryInEveryRoofMode()
    {
        using var fixture = NativePresentationFixtures.Create();
        var ceiling = new MapSurfacePatch
        {
            Key = new("cave-ceiling", 0, 0),
            Width = 16,
            Depth = 4,
            Heights = Enumerable.Repeat(300, 85).ToArray(),
            Cells = new MapSurfaceCell[64],
            Presence = [ulong.MaxValue]
        };
        fixture.Document.Surfaces.Patches.Add(ceiling.Key, ceiling);
        var surface = new MapSurfaceRef("cave-ceiling", MapLatticeFrame.ImportedMetreCentimetre, MapSurfaceRole.Ceiling,
            MapPresencePolicy.Native, null, null, new string('a', 64));
        fixture.Document.Surfaces.Refs.Add(surface with
        {
            SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(surface,
            [System.Collections.Generic.KeyValuePair.Create(ceiling.Key, MapSurfaceSemantics.PatchDigest(ceiling))])
        });
        var scope = NativePresentationFixtures.Scope with { Channels = NativePresentationFixtures.Scope.Channels | MapSceneChannels.Terrain };
        using var plan = NativePresentationFixtures.Plan(fixture, scope: scope); var picker = new MapPresentationPicker(plan);
        var sky = new MapPresentationRay(new(WorldFrame.Origin, new(1, 1, -1)), Vector3.UnitY, 10);
        foreach (var mode in Enum.GetValues<MapRoofMode>())
        {
            var frame = NativePresentationFixtures.Frame(mode: mode);
            foreach (var channel in new[] { MapPresentationChannel.Camera, MapPresentationChannel.Sky, MapPresentationChannel.Physical })
            {
                var hit = picker.Pick(sky, channel, frame)!.Value;
                Assert.Equal("cave-ceiling", hit.OwnerId); Assert.Equal(2, hit.Distance); Assert.Equal(-Vector3.UnitY, hit.Normal);
                var floor = picker.Pick(sky with { Direction = -Vector3.UnitY }, channel, frame)!.Value;
                Assert.Equal("ground", floor.OwnerId); Assert.Equal(1, floor.Distance); Assert.Equal(Vector3.UnitY, floor.Normal);
            }
            Assert.Null(picker.Pick(sky, MapPresentationChannel.Selection, frame));
        }
    }

    [Fact]
    public void WarmedAcceptedHlodAndLodCrossings_DoNotAllocateOrRebuild()
    {
        using var fixture = NativePresentationFixtures.Create(true); using var plan = NativePresentationFixtures.Plan(fixture, true);
        plan.Inventory.Placements.AcceptPublishedClusters();
        var builds = plan.Inventory.Placements.Clusters.ToArray(); var sink = new CountSink(); plan.Register(sink);
        var near = NativePresentationFixtures.Frame() with { Focus = new(WorldFrame.Origin, new(8, 0, 0)) };
        var lod = near with { Focus = new(WorldFrame.Origin, new(18, 0, 0)) };
        var midpoint = near with { Focus = new(WorldFrame.Origin, new(62, 0, 32)) };
        var full = near with { Focus = new(WorldFrame.Origin, new(72, 0, 32)), RoofMode = MapRoofMode.AlwaysHidden };
        void Exercise() { plan.Record(near, sink); plan.Record(lod, sink); plan.Record(midpoint, sink); plan.Record(full, sink); }
        Exercise(); Exercise();
        Assert.Equal(0, AllocationMeasurement.Measure("native accepted HLOD and LOD crossings", Exercise));
        Assert.Equal(builds, plan.Inventory.Placements.Clusters);
    }
    sealed class CountSink : IMapDrawSink
    {
        internal int Count;
        public void Register(MapPreparedResource resource) { }
        public void Write(in MapDrawRecord draw) => Count++;
    }
    sealed class RejectRoof : IMapPresentationCut { public bool Accept(string ownerId, MapFramePoint point) => ownerId != "roof-a"; }
}
