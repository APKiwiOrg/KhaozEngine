using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeAnchorReplacementTests
{
    [Theory]
    [InlineData(true, "outline-hlod", 2, 0)]
    [InlineData(true, "hlod", 1.25f, 80)]
    [InlineData(false, "hlod", 1.25f, 80)]
    public void GridReplacement_ReuploadsAnchorRelativePayloadAtTheSameWorldPose(bool roof, string part, float tileSize, long anchorX)
    {
        // This fixture's unit-scale triangles have integral coordinates, so both grids give exactly equal world bounds.
        var closure = NativeRenderAssetFixtures.Create("foliage");
        using var native = NativeSceneFixtures.SeparatedPlacements(closure, 1, new MapEffectiveTransform(80, 3, 8, 0, 1));
        native.Document.Placements[0].AssetId = "A";
        if (roof) native.Document.BuildingIntent = new(1, [], [], [], [])
        {
            Roofs = [new(MapPrefabReferenceScope.World, MapPrefabChildKind.Placement, "sparse-0")]
        };
        using var fixture = new ResidencyFixture(native.Document, closure);
        var scope = new MapSceneScope(new(WorldFrame.Origin, new(0), new(128), null, null,
            Enum.GetValues<MapSurfaceRole>(), null, new()), new(0, 0, 0, 128, 32, 128),
            MapSceneChannels.Placements | MapSceneChannels.Annotations, 1);
        var defaults = ResidencyFixture.Policy();
        var policy = new MapRenderPolicy(new([new("caps", new HashSet<string> { "A" }, [10], 0, 1000, 30, 10, 0, true)], 1000),
            defaults.AnimatedAssets, defaults.Foliage, defaults.Water, [], new Dictionary<ushort, string>());
        MapRenderSourceState State() => fixture.State(scope) with { Policy = policy };
        var initial = State();
        var scheduler = new ResidencyScheduler(); var device = new PayloadDevice();
        using var residency = new MapRenderResidency(initial, device, null, scheduler);
        ResidencyFixture.Complete(residency, scheduler);
        var originalPlan = Assert.Single(residency.AcceptedPlans);
        var originalMembers = Assert.Single(originalPlan.Inventory.Placements.Clusters).Request.Instances.ToArray();
        var before = Record(residency, part);
        var original = Assert.Single(before.Resources, r => r.Reference.Key.PartId == part);
        var oldDraw = Assert.Single(before.Draws, d => d.Resource == original.Reference);
        Assert.Equal(new MapSubmissionAnchor(64, 3, 0), oldDraw.Anchor);
        Assert.True(residency.TryGetHandle(original.Reference, out var oldHandle));
        Assert.Equal(16, Mesh(device.PublishedResources[oldHandle]).Vertices[0].Position.X);
        AssertWorldPositions(device.PublishedResources[oldHandle], oldDraw);

        fixture.Document.TileSize = tileSize;
        var replacement = State();
        Assert.Same(initial.Closure, replacement.Closure);
        residency.RequestReplacement(replacement);
        ResidencyFixture.Complete(residency, scheduler);
        var plan = Assert.Single(residency.AcceptedPlans);
        Assert.Equal(originalMembers, Assert.Single(plan.Inventory.Placements.Clusters).Request.Instances);
        var after = Record(residency, part);
        var current = Assert.Single(after.Resources, r => r.Reference.Key.PartId == part);
        var draw = Assert.Single(after.Draws, d => d.Resource == current.Reference);
        Assert.Equal(original.Reference.Key, current.Reference.Key);
        Assert.Equal(original.Bounds, current.Bounds);
        Assert.Equal(new MapSubmissionAnchor(anchorX, 3, 0), draw.Anchor);
        Assert.True(residency.TryGetHandle(current.Reference, out var handle));
        Assert.NotEqual(oldHandle, handle);
        Assert.Equal(80 - anchorX, Mesh(device.PublishedResources[handle]).Vertices[0].Position.X);
        AssertWorldPositions(device.PublishedResources[handle], draw);

        residency.RequestReplacement(State());
        ResidencyFixture.Complete(residency, scheduler);
        var stable = Assert.Single(Record(residency, part).Resources, r => r.Reference.Key.PartId == part);
        Assert.True(residency.TryGetHandle(stable.Reference, out var stableHandle));
        Assert.Equal(handle, stableHandle);
    }

    static MapDrawSet Record(MapRenderResidency residency, string part)
    {
        if (part != "outline-hlod") return ResidencyFixture.Draw(residency);
        var recorder = new MapDrawRecorder(); var plan = Assert.Single(residency.AcceptedPlans);
        var point = new MapFramePoint(WorldFrame.Origin, Vector3.Zero);
        plan.Register(recorder);
        plan.Inventory.Placements.RecordRoofOutline("sparse-0", new(WorldFrame.Origin, point, MapRoofMode.AlwaysVisible, 1, 1, point), recorder);
        return recorder.Snapshot();
    }

    static GltfMesh Mesh(MapPreparedResource resource) => Assert.IsType<MapRigidResource>(resource.Payload).Mesh;

    static void AssertWorldPositions(MapPreparedResource uploaded, MapDrawRecord draw)
    {
        Vector3[] triangle = [new(80, 3, 8), new(81, 3, 8), new(80, 4, 8)];
        var world = Mesh(uploaded).Vertices.Select(v => Vector3.Transform(v.Position, draw.LocalTransform)
            + new Vector3(draw.Anchor.X, draw.Anchor.Y, draw.Anchor.Z)).ToArray();
        Assert.Equal(triangle.Concat(triangle), world);
    }

    sealed class PayloadDevice : IMapRenderDevice
    {
        readonly ResidencyDevice _inner = new();
        internal readonly Dictionary<MapRenderHandle, MapPreparedResource> PublishedResources = new();
        public SceneUploadDemand MeasureUpload(MapPreparedResource resource) => _inner.MeasureUpload(resource);
        public IMapRenderUpload BeginUpload(MapPreparedResource resource, ISceneUploadReservation reservation) =>
            new Upload(this, resource, _inner.BeginUpload(resource, reservation));
        public void Release(MapRenderHandle handle)
        {
            _inner.Release(handle);
            PublishedResources.Remove(handle);
        }
        sealed class Upload(PayloadDevice device, MapPreparedResource resource, IMapRenderUpload inner) : IMapRenderUpload
        {
            public bool Ready => inner.Ready;
            public long Advance(long transferBytes) => inner.Advance(transferBytes);
            public MapRenderHandle Publish()
            {
                var handle = inner.Publish();
                device.PublishedResources.Add(handle, resource);
                return handle;
            }
            public void Dispose() => inner.Dispose();
        }
    }
}
