using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Physics;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Physics;

public class MapPrefabDoorwayTests
{
    [Theory]
    [InlineData(0.8f)]
    [InlineData(1.2f)]
    public void FreeDoorway_ProvesPhysicalClearanceOnly(float scale)
    {
        var world = PrefabPhysicsFixtures.Build(PrefabFixtures.DryTransformed(0.371f, scale, new(10.25f, 5, -3.125f)));
        using var physics = new BepuPhysicsWorld();
        using var registration = MapPhysicsRegistration.Register(world, physics);
        using var lease = ((IPhysicsQueryLeaseSource)physics).AcquireQueryReadLease();
        var relation = new MapPhysicalRelations(registration);
        Assert.Equal(MapPhysicalCertainty.Clear, relation.Clearance(lease, PrefabPhysicsFixtures.DoorPath(world, false), MoveTuning.Default).Certainty);
        Assert.Equal(MapPhysicalCertainty.Blocked, relation.Clearance(lease, PrefabPhysicsFixtures.DoorPath(world, true), MoveTuning.Default).Certainty);
        Assert.Equal(1f, MapInteractionPolicy.MinimumVerticalReachHeightMetres);
        Assert.Equal(3, Assert.IsType<CompoundShape>(world.Placements.Single(p => p.AssetId == "doorway").Collider).Children.Length);
    }

    [Theory]
    [InlineData(0.8f)]
    [InlineData(1.2f)]
    public void ScaledLowItem_StillHasOneMetreReachMinimum(float scale)
    {
        var world = PrefabPhysicsFixtures.Build(PrefabFixtures.DryTransformed(0.371f, scale, new(10.25f, 5, -3.125f)));
        var item = world.Placements.Single(p => p.AssetId == "item");
        Assert.Null(item.Collider);
        Assert.Equal(1, item.Envelope.Bounds.MaxY - item.Envelope.Bounds.MinY, 6);
        Assert.Equal(1 - 0.2 * scale, item.Envelope.RaiseMetres, 6);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BackendAdapter_BoundsIndependentlyTransformedVertices(bool far)
    {
        var pose = MapEffectiveTransform.Compose(MapEffectiveTransform.From(new(
            far ? new(30000.25f, -450, -29999.75f) : new(10.25f, 5, -3.125f), 0.371f, 1.2f)), new MapTransform(new(1.123456f, 0.123456f, -2.765432f), 0.271f, 0.8f));
        var frame = WorldFrame.Nearest((float)pose.X, (float)pose.Z);
        var backend = MapBackendPoseAdapter.Pose(pose, frame);
        double error = MapBackendPoseAdapter.ErrorMetres(pose, frame, 4);
        Assert.InRange(error, 0, far ? 0.0001 : 0.00001);
        foreach (var vertex in new[] { new Vector3(2, 1, -2), new(-2, -1, 2) })
        {
            var (s, c) = Math.SinCos(pose.YawRadians);
            double x = pose.X - frame.Anchor.X + pose.Scale * (c * vertex.X + s * vertex.Z);
            double y = pose.Y + pose.Scale * vertex.Y;
            double z = pose.Z - frame.Anchor.Z + pose.Scale * (-s * vertex.X + c * vertex.Z);
            Vector3 actual = backend.Position + Vector3.Transform(vertex * (float)pose.Scale, backend.Orientation);
            double distance = Math.Sqrt(Math.Pow(actual.X - x, 2) + Math.Pow(actual.Y - y, 2) + Math.Pow(actual.Z - z, 2));
            Assert.True(distance <= error, $"distance {distance:R}, error {error:R}");
        }
    }

    [Fact]
    public void TransformedCavePrefabOrLayer_PreservesCollisionNavAndMembership()
    {
        var f = PrefabPhysicsFixtures.TightBounds(PrefabFixtures.DryTransformed(0.371f, 1.2f, new(30000.25f, -450, -29999.75f)));
        var world = PrefabPhysicsFixtures.Build(f);
        var reloaded = PrefabPhysicsFixtures.Build(new(MapDocumentFile.LoadText(MapDocumentFile.SaveText(f.Document)), f.Assets));
        var grids = new MapWorldGrids(64, 1, 4, Vector2.Zero);
        var options = new MapNavTileOptions(1, "prefab", "physical-clearance-only");
        Assert.Equal(MapNavTiling.Partition(world, grids, options).Select(t => (t.Coord, t.GeometryDigest)),
            MapNavTiling.Partition(reloaded, grids, options).Select(t => (t.Coord, t.GeometryDigest)));
        using var physics = new BepuPhysicsWorld();
        physics.Rebase(new(30000, -450, -30000));
        using var registration = MapPhysicsRegistration.Register(reloaded, physics);
        var pose = reloaded.Document.EffectiveResources!.Transforms["Surface:a/floor"];
        var point = PrefabPhysicsFixtures.At(pose, 3, 1.5, -3);
        Vector3 query = point.Frame.Anchor + point.Local - physics.Origin;
        Assert.True(physics.Raycast(query, -Vector3.UnitY, 3, out var floor));
        Assert.True(physics.Raycast(query, Vector3.UnitY, 3, out var ceiling));
        Assert.Equal("a/floor", FloorOwner(floor));
        Assert.Equal("a/ceiling", FloorOwner(ceiling));
        Assert.Contains(reloaded.Terrain.Chunks.SelectMany(c => c.TriangleRoles), r => r == MapFaceRole.SupportFloor);
        Assert.Contains(reloaded.Terrain.Chunks.SelectMany(c => c.TriangleRoles), r => r == MapFaceRole.Ceiling);
        var member = new MapSpaceMembership(reloaded.Surfaces).Query(point);
        Assert.Equal("a/room", member.SpaceId);
        Assert.Contains("exterior", member.ParentSpaceIds);
        using var lease = ((IPhysicsQueryLeaseSource)physics).AcquireQueryReadLease();
        Assert.Equal(MapPhysicalCertainty.Clear, new MapPhysicalRelations(registration).Clearance(lease,
            PrefabPhysicsFixtures.DoorPath(reloaded, false), MoveTuning.Default).Certainty);
        Assert.Equal(MapPhysicalCertainty.Blocked, new MapPhysicalRelations(registration).Clearance(lease,
            PrefabPhysicsFixtures.DoorPath(reloaded, true), MoveTuning.Default).Certainty);

        string FloorOwner(RayHit hit)
        {
            Assert.True(registration.TryOwner(hit.Body!.Value, out var owner));
            var chunk = reloaded.Terrain.Chunks.Single(c => c.ChunkId == owner.OwnerId);
            return chunk.TriangleOwners[0].OwnerId;
        }
    }
}
