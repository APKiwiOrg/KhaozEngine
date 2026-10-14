using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Physics;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Tests.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Physics;

public class MapPrefabSupportTests
{
    [Fact]
    public void UnpackedSupport_StaticsAndFaceOwnersStaySingle()
    {
        var f = PrefabFixtures.SupportWithEmbeddedRecord();
        var before = PrefabPhysicsFixtures.Build(f);
        var prepared = MapPrefabEdits.Prepare(f.Document, f.Assets, new MapUnpackPrefab("a"));
        foreach (var document in new[] { prepared.After, MapDocumentFile.LoadText(MapDocumentFile.SaveText(prepared.After)) })
        {
            var after = PrefabPhysicsFixtures.Build(new(document, f.Assets));
            Assert.NotEmpty(before.Statics);
            Assert.Equal(before.Statics.Select(s => (s.OwnerId, s.Digest)), after.Statics.Select(s => (s.OwnerId, s.Digest)));
            Assert.NotEmpty(before.Terrain.Chunks.SelectMany(c => c.TriangleOwners));
            Assert.Equal(before.Terrain.Chunks.SelectMany(c => c.TriangleOwners), after.Terrain.Chunks.SelectMany(c => c.TriangleOwners));
            Assert.Single(after.Surfaces.Surfaces, s => s.Id == PrefabFixtures.SupportFloorId);
        }
    }

    [Fact]
    public void LocalFloor_HasCanonicalFaceAndOccupiedRoomOwner()
    {
        var f = PrefabFixtures.Create("support-resource");
        var world = PrefabPhysicsFixtures.Build(f);
        using var physics = new BepuPhysicsWorld();
        using var registration = MapPhysicsRegistration.Register(world, physics);
        Assert.True(physics.Raycast(new(2, 2, -2), -Vector3.UnitY, 4, out var hit));
        Assert.NotNull(hit.Body);
        using var lease = ((IPhysicsQueryLeaseSource)physics).AcquireQueryReadLease();
        Span<CapsuleIncidentFace> faces = stackalloc CapsuleIncidentFace[256];
        var contact = ((IPhysicsCapsuleFeatures)physics).QueryCapsuleFeature(lease,
            hit.Body.Value, new CapsuleShape(0.2f, 0.4f), Pose.At(new(2, 0.405f, -2)), 0.01f, faces);
        Assert.Equal(CapsuleFeatureStatus.Complete, contact.Status);
        Assert.True(contact.Written > 0);
        Assert.True(registration.TryFaceOwner(hit.Body.Value, faces[0].FaceId, out var face));
        Assert.Equal("prefab/a/Surface/deck%2Fdeck-support%2Ffloor", face.OwnerId);
        var p = PrefabFixtures.Point(world.Document.EffectiveResources!, 2, 0.01f, -2);
        Assert.Equal("a/room", new MapSpaceMembership(world.Surfaces).Query(p).SpaceId);
        Assert.DoesNotContain(world.Statics, s => s.OwnerId == "prefab/a/Placement/deck");
    }

    [Theory]
    [InlineData(false, 5)]
    [InlineData(true, -450)]
    public void ImplicitSupportMissingY_BuilderMatchesResolver(bool far, float expectedY)
    {
        var f = PrefabFixtures.SupportAt(far);
        long highWater = f.Document.NumericIdHighWaterMark;
        var pure = PrefabFixtures.Effective(f);
        var world = PrefabPhysicsFixtures.Build(f);
        Assert.Contains(world.Surfaces.Surfaces, s => s.Id == PrefabFixtures.SupportFloorId);
        Assert.NotEmpty(world.Document.EffectiveResources!.PlacementSupports);
        Assert.Equal(expectedY, world.Placements.Single(p => p.PlacementId == "low-item").WorldPose.Position.Y);
        Assert.Equal(expectedY, pure.Placements.Single(p => p.PlacementId == "low-item").Transform.Position.Y);
        Assert.Equal(highWater, f.Document.NumericIdHighWaterMark);
    }

    [Fact]
    public void CoincidentSupportOwners_Refuse()
    {
        Assert.Contains("coincident", Assert.Throws<MapDocumentException>(() => PrefabPhysicsFixtures.Build(PrefabFixtures.CoincidentSupport())).Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("coincident", Assert.Throws<MapDocumentException>(() => PrefabPhysicsFixtures.Build(PrefabPhysicsFixtures.CentimetreSupport(true))).Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PaintAddsNoPhysicsFaces()
    {
        var f = PrefabFixtures.Create("support-resource");
        var original = PrefabPhysicsFixtures.Build(f);
        var surface = PrefabFixtures.Surface("paint", MapSurfaceRole.PaintOverride) with { PaintTargetSurfaceId = "exterior" };
        var patch = PrefabFixtures.Patch("paint", 4, 4, -200);
        f.Document.Surfaces.Patches.Add(patch.Key, patch);
        f.Document.Surfaces.Refs.Add(surface with
        {
            SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(surface,
            new[] { new System.Collections.Generic.KeyValuePair<MapPatchKey, string>(patch.Key, MapSurfaceSemantics.PatchDigest(patch)) })
        });
        var painted = PrefabPhysicsFixtures.Build(f);
        Assert.Equal(original.Terrain.Chunks.Select(c => c.Digest), painted.Terrain.Chunks.Select(c => c.Digest));
        Assert.DoesNotContain(painted.Terrain.Chunks.SelectMany(c => c.TriangleOwners), f => f.OwnerId == "paint");
    }

    [Fact]
    public void SourceUnitsAreEncodedInSupportFrames_AndNotAppliedAgain()
    {
        var f = PrefabPhysicsFixtures.CentimetreSupport();
        var instance = f.Document.Prefabs!.Instances.Single();
        f.Document.Prefabs = new(1, 1, new[] { instance with { Transform = new(Vector3.Zero, 0, 1.2f) } }, []);
        var world = PrefabPhysicsFixtures.Build(f);
        var deck = world.Placements.Single(p => p.AssetId == "deck");
        var chunk = Assert.Single(world.Terrain.Chunks, c => c.TriangleOwners.Any(f => f.OwnerId == PrefabFixtures.SupportFloorId));
        double edge = chunk.Shape.Vertices.Max(v => v.X) + chunk.Anchor.X;
        Assert.Equal(1.2, edge, 6);
        var physical = MapShapeBounds.Of(deck.Collider!, deck.WorldPose);
        Assert.Equal(1.2, physical.MaxX, 6);
        Assert.Equal(edge, physical.MaxX, 6);
        Assert.True(deck.ColliderBounds!.Value.MaxX >= physical.MaxX);
    }

    [Theory]
    [InlineData(32, 4f)]
    [InlineData(16, 8f)]
    public void LocalChunks_Keep1024TrianglesAnd64MetreOffsets(int cells, float scale)
    {
        var f = PrefabFixtures.Create("roomless");
        f.Document.NativeResourceTransforms = new[] { new KhaozEngine.MapDoc.Prefabs.MapStandaloneResourceTransform(
            KhaozEngine.MapDoc.Prefabs.MapPrefabChildKind.Surface, "large-floor", new(30000.25, -450, -29999.75, 0.371f, scale), new string('a', 64)) };
        // One control splits for the triangle cap. The other splits for extent while already under the cap.
        var patch = PrefabFixtures.Patch("large-floor", cells, cells, 0);
        f.Document.Surfaces.Patches.Add(patch.Key, patch);
        var surface = PrefabFixtures.Surface("large-floor", MapSurfaceRole.SupportFloor);
        f.Document.Surfaces.Refs.Add(surface with
        {
            SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(surface,
            new[] { new System.Collections.Generic.KeyValuePair<MapPatchKey, string>(patch.Key, MapSurfaceSemantics.PatchDigest(patch)) })
        });
        var world = PrefabPhysicsFixtures.Build(f);
        Assert.NotEmpty(world.Terrain.Chunks);
        foreach (var chunk in world.Terrain.Chunks)
        {
            Assert.InRange(chunk.Shape.Indices.Length / 3, 1, 1024);
            Assert.All(chunk.Shape.Vertices, v => Assert.True(Math.Abs(v.X) <= 64 && Math.Abs(v.Y) <= 64 && Math.Abs(v.Z) <= 64));
            Assert.Equal(chunk.Shape.Indices.Length / 3, chunk.TriangleOwners.Count);
        }
    }

    [Theory]
    [InlineData("peer-overlap")]
    [InlineData("outside-parent")]
    public void CompleteBuildRefusesInvalidRoomCoverageWithoutWater(string variant)
    {
        var f = PrefabFixtures.Cave(variant);
        Assert.NotNull(PrefabFixtures.Effective(f));
        Assert.Throws<MapDocumentException>(() => PrefabPhysicsFixtures.Build(f));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void MissingYProvider_RefusesBeforePhysicsInstallation(bool materialized, bool space)
    {
        var f = PrefabFixtures.MissingYProvider(materialized, space);
        long highWater = f.Document.NumericIdHighWaterMark;
        var error = Assert.Throws<MapDocumentException>(() => PrefabPhysicsFixtures.Build(f));
        Assert.Contains("consumer", error.Message);
        Assert.Contains("provider", error.Message);
        Assert.Contains("missing Y", error.Message);
        Assert.Equal(highWater, f.Document.NumericIdHighWaterMark);
    }

    [Fact]
    public void StandaloneSupport_HasStableStringOwnersAndNoAllocation()
    {
        var f = PrefabFixtures.Create("attached");
        var deck = (KhaozEngine.MapDoc.Prefabs.MapPrefabPlacementChild)f.Assets.GetPrefab("hut").Children.Single(c => c.Key.Key == "deck");
        f.Document.Prefabs = null;
        f.Document.Placements.Add(deck.Value);
        long highWater = f.Document.NumericIdHighWaterMark;
        var first = PrefabPhysicsFixtures.Build(f);
        var reloaded = PrefabPhysicsFixtures.Build(new(MapDocumentFile.LoadText(MapDocumentFile.SaveText(f.Document)), f.Assets));
        Assert.Equal(new[] { "deck/deck-support/top" }, first.Surfaces.Surfaces.Select(s => s.Id));
        Assert.Equal(first.Terrain.Chunks.Select(c => c.Digest), reloaded.Terrain.Chunks.Select(c => c.Digest));
        Assert.Equal(highWater, f.Document.NumericIdHighWaterMark);
        Assert.Null(first.Document.Placements.Single().NumericId);
    }

    [Fact]
    public void MaterializedSupport_HasOneCanonicalProducer()
    {
        var world = PrefabPhysicsFixtures.Build(PrefabFixtures.MaterializedOverrideFixture());
        Assert.Equal(new[] { "captured-floor" }, world.Surfaces.Surfaces.Select(s => s.Id));
        Assert.All(world.Terrain.Chunks.SelectMany(c => c.TriangleOwners), f => Assert.Equal("captured-floor", f.OwnerId));
        Assert.DoesNotContain(world.Surfaces.Surfaces, s => s.Id.Contains("deck-support", StringComparison.Ordinal));
    }

    [Fact]
    public void TransformedSupportOwner_AgreesAtFarSite()
    {
        var f = PrefabFixtures.Create("support-resource");
        var instance = f.Document.Prefabs!.Instances.Single() with { Transform = new(new(30000.25f, -450, -29999.75f), 0.371f, 1.2f) };
        f.Document.Prefabs = new(1, 1, new[] { instance }, []);
        PrefabFixtures.AuthorWorldParent(f, instance.Transform);
        var world = PrefabPhysicsFixtures.Build(f);
        using var physics = new BepuPhysicsWorld();
        physics.Rebase(new(30000, -450, -30000));
        using var registration = MapPhysicsRegistration.Register(world, physics);
        var pose = world.Document.EffectiveResources!.Transforms["Surface:" + PrefabFixtures.SupportFloorId];
        var point = PrefabPhysicsFixtures.At(pose, 2, 1.5, -2);
        Assert.True(physics.Raycast(point.Frame.Anchor + point.Local - physics.Origin, -Vector3.UnitY, 3, out var hit));
        Assert.True(registration.TryOwner(hit.Body!.Value, out var owner));
        var chunk = world.Terrain.Chunks.Single(c => c.ChunkId == owner.OwnerId);
        Assert.All(chunk.TriangleOwners, f => Assert.Equal(PrefabFixtures.SupportFloorId, f.OwnerId));
        Assert.Equal("a/room", new MapSpaceMembership(world.Surfaces).Query(point).SpaceId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BakedHullFloor_CoincidentSupportRefusesAtNearAndFarSites(bool far)
    {
        var hull = new ConvexHullShape(new[] { new Vector3(0, 0, 0), new(100, 0, 0), new(0, 0, -100), new(100, 0, -100),
            new(0, -100, 0), new(100, -100, 0), new(0, -100, -100), new(100, -100, -100) });
        var f = PrefabPhysicsFixtures.CentimetreSupport(true, hull);
        var instance = f.Document.Prefabs!.Instances.Single() with
        {
            Transform = new(
            far ? new(30000.25f, -450, -29999.75f) : new(10.25f, 5, -3.125f), 0.371f, 1.2f)
        };
        f.Document.Prefabs = new(1, 1, new[] { instance }, []);
        Assert.Contains("coincident", Assert.Throws<MapDocumentException>(() => PrefabPhysicsFixtures.Build(f)).Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FarAdjacentColliderFloor_DoesNotBecomeCoincidentThroughAbsoluteFloatRounding()
    {
        var mesh = new TriangleMeshShape(new[] { new Vector3(100.001f, 0, 0), new(200.001f, 0, 0),
            new(100.001f, 0, -100), new(200.001f, 0, -100) }, new[] { 0, 2, 1, 1, 2, 3 });
        var f = PrefabPhysicsFixtures.CentimetreSupport(true, mesh);
        var instance = f.Document.Prefabs!.Instances.Single() with
        {
            Transform = new(new(30000.25f, -450, -29999.75f), 0, 1.2f),
            Overrides = new KhaozEngine.MapDoc.Prefabs.MapPrefabOverride[]
            {
                new KhaozEngine.MapDoc.Prefabs.MapPrefabTransformOverride(new(KhaozEngine.MapDoc.Prefabs.MapPrefabChildKind.Placement, "deck"), new(new(0.0004f, 0, 0), 0, 1)),
                new KhaozEngine.MapDoc.Prefabs.MapPrefabPlacementOverride(new(KhaozEngine.MapDoc.Prefabs.MapPrefabChildKind.Placement, "low-item"), new(new(0.5f, 0, -0.5f), 0, 1), null, null),
            }
        };
        f.Document.Prefabs = new(1, 1, new[] { instance }, []);
        // The source meshes have a positive gap. A backend's absolute float origin is not an ownership witness.
        Assert.NotEmpty(PrefabPhysicsFixtures.Build(f).Terrain.Chunks);
    }
}
