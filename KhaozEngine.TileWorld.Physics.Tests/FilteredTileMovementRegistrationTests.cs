using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Physics;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Physics;

public class FilteredTileMovementRegistrationTests
{
    [Fact]
    public void FilteredOwnersAndGroundExclusionsAreCanonical()
    {
        var fixture = CreateFixture();
        int[] selected = [0, 2, 3, 5];
        using var world = new BepuPhysicsWorld();
        using var installed = Select(fixture.Colliders, world, selected);
        var registration = installed.CreateMovementRegistration("tiles");
        using var view = installed.CreateMovementQueryView();
        using var lease = world.AcquireQueryReadLease();
        Assert.Equal(selected, installed.ColliderIndices);
        Assert.Equal(new[] { installed.Handles[0] }, installed.GroundHandles);
        Assert.Equal(installed.GroundHandles, registration.ExcludedStatics);
        Assert.Equal(MovementAvailability.Unresolved, registration.TryBind(lease, installed.Handles[0], 0, out _));
        AssertOwners(fixture, installed, registration, lease);
        Assert.Equal(Expected(world, fixture, installed).Build(lease).SemanticDigest, registration.SemanticDigest);
        var rejected = fixture.Rows[1];
        var withRejected = Expected(world, fixture, installed).Add(installed.Handles[1], 0, int.MaxValue,
            new("tiles", rejected.OwnerLocalId), rejected.AbsoluteMinimum, rejected.AbsoluteMaximum).Build(lease);
        Assert.NotEqual(withRejected.SemanticDigest, registration.SemanticDigest);
        Assert.False(view.Raycast(new(20, 20, 20), -Vector3.UnitY, 100, out _));
        Assert.True(view.Raycast(new(9.5f, 20, 8.5f), -Vector3.UnitY, 100, out var hit));
        Assert.Equal(installed.Handles[2], hit.Body);
    }

    [Fact]
    public void PartialPlacedOwnerSelectionDoesNotRenameOrMergeMembers()
    {
        var fixture = CreateFixture();
        int[] selected = [2, 5];
        using var world = new BepuPhysicsWorld();
        using var installed = Select(fixture.Colliders, world, selected);
        var registration = installed.CreateMovementRegistration("tiles");
        using var lease = world.AcquireQueryReadLease();
        Assert.Equal(selected, installed.ColliderIndices);
        Assert.Empty(installed.GroundHandles);
        Assert.Empty(registration.ExcludedStatics);
        AssertOwners(fixture, installed, registration, lease);
        Assert.Equal(Expected(world, fixture, installed).Build(lease).SemanticDigest, registration.SemanticDigest);
        Assert.Equal(new[] { "placed/1", "placed/2" }, selected.Select(i => fixture.Rows[i].OwnerLocalId));
    }

    [Fact]
    public void FilteredOwnerDigestIgnoresRegionLoadAndHandleAllocationOrder()
    {
        var forward = CreateFixture(new(0, -1), new(1, -1));
        var reverse = CreateFixture(new(1, -1), new(0, -1));
        int[] selected = [1, 3, 4, 6];
        using var worldA = new BepuPhysicsWorld();
        using var worldB = new BepuPhysicsWorld();
        worldB.AddStatic(new BoxShape(Vector3.One), Pose.At(new(1000, 10, 1000)));
        using var installedA = Select(forward.Colliders, worldA, selected);
        using var installedB = Select(reverse.Colliders, worldB, selected);
        var registrationA = installedA.CreateMovementRegistration("tiles");
        var registrationB = installedB.CreateMovementRegistration("tiles");
        Assert.Equal(forward.Colliders.Hash, reverse.Colliders.Hash);
        Assert.Equal(selected, installedA.ColliderIndices);
        Assert.Equal(installedA.ColliderIndices, installedB.ColliderIndices);
        Assert.NotEqual(installedA.Handles.ToArray(), installedB.Handles.ToArray());
        Assert.Equal(registrationA.SemanticDigest, registrationB.SemanticDigest);
        using var leaseA = worldA.AcquireQueryReadLease();
        using var leaseB = worldB.AcquireQueryReadLease();
        AssertOwners(forward, installedA, registrationA, leaseA);
        AssertOwners(reverse, installedB, registrationB, leaseB);
        Assert.Equal(Expected(worldA, forward, installedA).Build(leaseA).SemanticDigest, registrationA.SemanticDigest);
        Assert.Equal(Expected(worldB, reverse, installedB).Build(leaseB).SemanticDigest, registrationB.SemanticDigest);
    }

    [Fact]
    public void SelectedBoundsStayAbsoluteAndContainTransformedVertices()
    {
        var fixture = CreateFixture();
        int[] selected = [0, 2, 3, 5];
        Vector3 origin = new(40.5f, 3.25f, -24.75f);
        using var world = new BepuPhysicsWorld();
        world.Rebase(origin);
        using var installed = Select(fixture.Colliders, world, selected);
        var registration = installed.CreateMovementRegistration("tiles");
        using var lease = world.AcquireQueryReadLease();
        Assert.Equal(Expected(world, fixture, installed).Build(lease).SemanticDigest, registration.SemanticDigest);
        Assert.Equal(fixture.Colliders.MovementExtent, registration.Extent);
        Assert.True(fixture.Rows[5].AbsoluteMaximum.X > 64);
        Assert.NotEqual(Quaternion.Identity, fixture.Colliders.Colliders[5].Pose.Orientation);
        foreach (int index in selected)
        {
            var collider = fixture.Colliders.Colliders[index];
            var bounds = fixture.Rows[index];
            Vector3[] local = collider.Shape switch
            {
                TriangleMeshShape mesh => mesh.Vertices,
                BoxShape box => (from x in new[] { -1, 1 }
                                 from y in new[] { -1, 1 }
                                 from z in new[] { -1, 1 }
                                 select new Vector3(x, y, z) * box.HalfExtents).ToArray(),
                _ => throw new InvalidOperationException()
            };
            foreach (Vector3 vertex in local)
            {
                Vector3 absolute = Vector3.Transform(vertex, collider.Pose.Orientation) + collider.Pose.Position;
                AssertContained(absolute, bounds.AbsoluteMinimum, bounds.AbsoluteMaximum);
                AssertContained(absolute, registration.Extent.Minimum, registration.Extent.Maximum);
            }
        }
        AssertContained(fixture.Colliders.Medium.Extent.Minimum, registration.Extent.Minimum, registration.Extent.Maximum);
        AssertContained(fixture.Colliders.Medium.Extent.Maximum, registration.Extent.Minimum, registration.Extent.Maximum);
    }

    [Fact]
    public void FilteredSnapshotsDoNotObserveLaterPolicyOrBuilderChanges()
    {
        var fixture = CreateFixture();
        using var world = new BepuPhysicsWorld();
        bool keepGround = true;
        int calls = 0;
        using var installed = fixture.Colliders.AddTo(world, c =>
        {
            calls++;
            return keepGround || c.Kind != TileColliderKind.Ground;
        });
        var builder = installed.CreateMovementRegistrationBuilder("tiles");
        using var lease = world.AcquireQueryReadLease();
        var registration = builder.Build(lease);
        string digest = registration.SemanticDigest;
        long generation = registration.Generation, geometryGeneration = registration.GeometryGeneration;
        var exclusions = registration.ExcludedStatics.ToArray();
        keepGround = false;
        var row = fixture.Rows[1];
        builder.Add(installed.Handles[1], 0, int.MaxValue, new("tiles", "edited"), row.AbsoluteMinimum, row.AbsoluteMaximum);
        builder.Exclude(installed.Handles[1]);
        Assert.NotEqual(digest, builder.Build(lease).SemanticDigest);
        Assert.Equal(digest, registration.SemanticDigest);
        Assert.Equal(exclusions, registration.ExcludedStatics);
        Assert.Equal(generation, registration.Generation);
        Assert.Equal(geometryGeneration, registration.GeometryGeneration);
        AssertOwners(fixture, installed, registration, lease);
        Assert.Equal(fixture.Colliders.Colliders.Count, calls);
        Assert.Equal(digest, installed.CreateMovementRegistrationBuilder("tiles").Build(lease).SemanticDigest);
        Assert.Equal(fixture.Colliders.Colliders.Count, calls);
    }

    [Fact]
    public void SameWorldIdAndHandleValuesCannotAuthenticateAnotherSource()
    {
        var fixture = CreateFixture();
        using var own = new BepuPhysicsWorld();
        using var other = new BepuPhysicsWorld();
        using var installed = Select(fixture.Colliders, own, [2, 3, 5]);
        using var otherInstalled = Select(fixture.Colliders, other, [2, 3, 5]);
        Assert.Equal(installed.Handles, otherInstalled.Handles);
        var builder = installed.CreateMovementRegistrationBuilder("tiles");
        using var ownLease = own.AcquireQueryReadLease();
        using var otherSourceLease = other.AcquireQueryReadLease();
        var registration = builder.Build(ownLease);
        Assert.Equal(MovementAvailability.Known, registration.Authenticate(ownLease));
        Assert.Equal(MovementAvailability.Invalid, registration.Authenticate(otherSourceLease));
        Assert.Throws<ArgumentException>(() => builder.Build(otherSourceLease));
    }

    [Fact]
    public void StaticMutationInvalidatesButStepAndRebaseDoNotRefreshSnapshot()
    {
        var fixture = CreateFixture();
        using var world = new BepuPhysicsWorld();
        using var installed = Select(fixture.Colliders, world, [2, 3, 5]);
        var registration = installed.CreateMovementRegistration("tiles");
        long generation = registration.Generation, geometryGeneration = registration.GeometryGeneration;
        long staticGeneration = world.StaticGeometryGeneration;
        world.Step(1f / 60f);
        using (var afterStep = world.AcquireQueryReadLease())
            Assert.Equal(MovementAvailability.Known, registration.Authenticate(afterStep));
        world.Rebase(new(40.5f, 3.25f, -24.75f));
        using (var afterRebase = world.AcquireQueryReadLease())
        {
            Assert.NotEqual(geometryGeneration, afterRebase.GeometryGeneration);
            Assert.Equal(MovementAvailability.Known, registration.Authenticate(afterRebase));
        }
        Assert.Equal(staticGeneration, world.StaticGeometryGeneration);
        StaticHandle external = world.AddStatic(new BoxShape(Vector3.One), Pose.At(new(5, 2, 5)));
        using (var afterAdd = world.AcquireQueryReadLease())
            Assert.Equal(MovementAvailability.Unresolved, registration.Authenticate(afterAdd));
        world.RemoveStatic(external);
        using (var afterRemove = world.AcquireQueryReadLease())
        {
            Assert.Equal(MovementAvailability.Unresolved, registration.Authenticate(afterRemove));
            var fresh = installed.CreateMovementRegistrationBuilder("tiles").Build(afterRemove);
            Assert.Equal(MovementAvailability.Known, fresh.Authenticate(afterRemove));
        }
        Assert.Equal(generation, registration.Generation);
        Assert.Equal(geometryGeneration, registration.GeometryGeneration);
        installed.Remove();
        Assert.Throws<InvalidOperationException>(() => installed.CreateMovementRegistration("tiles"));
    }

    private static (TileWorldColliders Colliders, TileColliderProvenance[] Rows) CreateFixture(params RegionCoord[] regions)
    {
        if (regions.Length == 0) regions = [new(0, -1)];
        var doc = TileWorldPhysicsTestData.FlatWorld(regions);
        doc.SetUnderlay(0, -1, 0, TileWorldPhysicsTestData.Water);
        doc.SetCornerHeightCm(0, -1, 0, -200);
        var corner = doc.AddObject("wall_corner", 5, -5, 0, 0);
        doc.SetSettings(9, -9, 0, TileSettings.Blocked);
        var deck = doc.AddObject("deck_skewed", 63, -12, 0, 0);
        var catalogs = TileWorldPhysicsTestData.Catalogs();
        catalogs.Archetype("deck_skewed")!.WalkSurfaces!.Add(new() { Height = 2.4f, MinX = 1, MaxX = 4 });
        var colliders = TileWorldColliders.Build(doc, catalogs, "tiles");
        string[] owners = [.. regions.OrderBy(r => r.Rz).ThenBy(r => r.Rx).Select(r => $"ground/{r.Rx}/{r.Rz}/0"),
            $"placed/{corner.Id}", $"placed/{corner.Id}", "blocked/0/9/-9", $"placed/{deck.Id}", $"placed/{deck.Id}"];
        Assert.Equal(owners.Length, colliders.Colliders.Count);
        var rows = colliders.Colliders.Select((c, i) => TileColliderProvenance.Capture(owners[i], c)).ToArray();
        return (colliders, rows);
    }

    private static TileColliderRegistration Select(TileWorldColliders colliders, BepuPhysicsWorld world, int[] indices)
    {
        int next = 0;
        return colliders.AddTo(world, _ => indices.Contains(next++));
    }

    private static OrdinaryMovementRegistration.Builder Expected(BepuPhysicsWorld world,
        (TileWorldColliders Colliders, TileColliderProvenance[] Rows) fixture, TileColliderRegistration installed)
    {
        var builder = OrdinaryMovementRegistration.CreateBuilder("tiles", world, fixture.Colliders.MovementExtent);
        for (int i = 0; i < installed.Handles.Count; i++)
        {
            int original = installed.ColliderIndices[i];
            var row = fixture.Rows[original];
            builder.Add(installed.Handles[i], 0, int.MaxValue, new("tiles", row.OwnerLocalId), row.AbsoluteMinimum, row.AbsoluteMaximum);
            if (fixture.Colliders.Colliders[original].Kind == TileColliderKind.Ground) builder.Exclude(installed.Handles[i]);
        }
        return builder;
    }

    private static void AssertOwners((TileWorldColliders Colliders, TileColliderProvenance[] Rows) fixture,
        TileColliderRegistration installed, OrdinaryMovementRegistration registration, IPhysicsQueryLease lease)
    {
        for (int i = 0; i < installed.Handles.Count; i++)
        {
            int original = installed.ColliderIndices[i];
            if (fixture.Colliders.Colliders[original].Kind == TileColliderKind.Ground) continue;
            Assert.Equal(MovementAvailability.Known, registration.TryBind(lease, installed.Handles[i], 0, out var owner));
            Assert.Equal(new MovementSupportKey("tiles", fixture.Rows[original].OwnerLocalId), owner);
            Assert.Equal(MovementAvailability.Known, registration.TryBind(lease, installed.Handles[i], int.MaxValue, out var last));
            Assert.Equal(owner, last);
        }
    }

    private static void AssertContained(Vector3 point, Vector3 minimum, Vector3 maximum)
    {
        Assert.InRange(point.X, minimum.X, maximum.X);
        Assert.InRange(point.Y, minimum.Y, maximum.Y);
        Assert.InRange(point.Z, minimum.Z, maximum.Z);
    }
}
