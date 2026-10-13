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

public class TileMovementRegistrationTests
{
    [Fact]
    public void LegacyNullOptionsRemainsSourceCompatible()
    {
        var doc = TileWorldPhysicsTestData.FlatWorld();
        var catalogs = TileWorldPhysicsTestData.Catalogs();
        Assert.Equal(TileWorldColliders.Build(doc, catalogs).Hash, TileWorldColliders.Build(doc, catalogs, null).Hash);
    }

    [Fact]
    public void AdditionalInstalledStaticsJoinTheTileOwnersThroughTheSourceBoundBuilder()
    {
        var colliders = TileWorldColliders.Build(TileWorldPhysicsTestData.FlatWorld(), TileWorldPhysicsTestData.Catalogs(), "tiles");
        using var physics = new BepuPhysicsWorld(Vector3.Zero);
        using var installed = colliders.AddTo(physics);
        var external = physics.AddStatic(new BoxShape(Vector3.One), Pose.At(new(70, 2, -10)));
        var extent = new OrdinaryMovementExtent(colliders.MovementExtent.Minimum, Vector3.Max(colliders.MovementExtent.Maximum, new(71, 3, 0)),
            colliders.MovementExtent.AnalyticMinimumY, colliders.MovementExtent.AnalyticMaximumY);
        var builder = installed.CreateMovementRegistrationBuilder("tiles", extent);
        builder.Add(external, 0, int.MaxValue, new("tiles", "external/ledge"), new(69, 1, -11), new(71, 3, -9));
        using var lease = physics.AcquireQueryReadLease();
        var combined = builder.Build(lease);
        Assert.Equal(MovementAvailability.Known, combined.TryBind(lease, external, 0, out var owner));
        Assert.Equal(new MovementSupportKey("tiles", "external/ledge"), owner);
        Assert.Equal(installed.GroundHandles, combined.ExcludedStatics);
    }

    [Fact]
    public void OwnersComeFromInstallerAndGroundIsExplicitlyExcluded()
    {
        var doc = TileWorldPhysicsTestData.FlatWorld();
        var corner = doc.AddObject("wall_corner", 5, 5, 0, 0);
        var deck = doc.AddObject("deck_skewed", 63, 12, 0, 0);
        doc.SetSettings(9, 9, 0, TileSettings.Blocked);
        var colliders = TileWorldColliders.Build(doc, TileWorldPhysicsTestData.Catalogs(), "tiles");
        using var physics = new BepuPhysicsWorld(Vector3.Zero);
        using var installed = colliders.AddTo(physics);
        var registration = installed.CreateMovementRegistration("tiles");
        Assert.Throws<ArgumentException>(() => installed.CreateMovementRegistration("wrong"));
        using (var lease = physics.AcquireQueryReadLease())
        {
            var expected = OrdinaryMovementRegistration.CreateBuilder("tiles", physics, colliders.MovementExtent);
            for (int i = 0; i < colliders.Colliders.Count; i++)
            {
                var collider = colliders.Colliders[i];
                string expectedLocal = collider.Kind switch
                {
                    TileColliderKind.Ground => "ground/0/0/0",
                    TileColliderKind.Wall => $"placed/{corner.Id}",
                    TileColliderKind.WalkSurface => $"placed/{deck.Id}",
                    _ => "blocked/0/9/9"
                };
                var bounds = TileColliderProvenance.Capture(expectedLocal, collider);
                expected.Add(installed.Handles[i], 0, int.MaxValue, new("tiles", expectedLocal), bounds.AbsoluteMinimum, bounds.AbsoluteMaximum);
                if (collider.Kind == TileColliderKind.Ground) expected.Exclude(installed.Handles[i]);
                var availability = registration.TryBind(lease, installed.Handles[i], 0, out var owner);
                if (collider.Kind == TileColliderKind.Ground)
                {
                    Assert.Equal(MovementAvailability.Unresolved, availability);
                    Assert.Contains(installed.Handles[i], registration.ExcludedStatics);
                    continue;
                }
                Assert.Equal(MovementAvailability.Known, availability);
                Assert.Equal(new MovementSupportKey("tiles", expectedLocal), owner);
            }
            Assert.Equal(expected.Build(lease).SemanticDigest, registration.SemanticDigest);
        }
        installed.Remove();
        using var after = physics.AcquireQueryReadLease();
        Assert.Equal(MovementAvailability.Unresolved, registration.Authenticate(after));
    }

    [Fact]
    public void ExtentContainsTransformedVerticesExteriorObjectsAnalyticRangeAndWater()
    {
        var doc = MovementWaterFieldTests.WaterWorld();
        doc.AddObject("deck_skewed", 63, -2, 0, 0);
        var colliders = TileWorldColliders.Build(doc, TileWorldPhysicsTestData.Catalogs(), "water-world");
        var extent = colliders.MovementExtent;
        foreach (var collider in colliders.Colliders)
        {
            Vector3[] points = collider.Shape switch
            {
                TriangleMeshShape mesh => mesh.Vertices,
                BoxShape box => (from x in new[] { -1, 1 }
                                 from y in new[] { -1, 1 }
                                 from z in new[] { -1, 1 }
                                 select new Vector3(x, y, z) * box.HalfExtents).ToArray(),
                _ => throw new InvalidOperationException()
            };
            foreach (Vector3 p in points)
            {
                var absolute = Vector3.Transform(p, collider.Pose.Orientation) + collider.Pose.Position;
                Assert.InRange(absolute.X, extent.Minimum.X, extent.Maximum.X);
                Assert.InRange(absolute.Y, extent.Minimum.Y, extent.Maximum.Y);
                Assert.InRange(absolute.Z, extent.Minimum.Z, extent.Maximum.Z);
            }
        }
        Assert.True(extent.Maximum.X > 64);
        Assert.True(extent.Minimum.Y <= colliders.Medium.Extent.Minimum.Y);
        Assert.True(extent.Maximum.Y >= colliders.Medium.Extent.Maximum.Y);
        Assert.InRange(extent.AnalyticMinimumY!.Value, extent.Minimum.Y, extent.Maximum.Y);
        Assert.InRange(extent.AnalyticMaximumY!.Value, extent.Minimum.Y, extent.Maximum.Y);
    }

    [Fact]
    public void CapturedGroundAndContentIdentitySurviveDocumentEditsAndRegionOrder()
    {
        var catalogs = TileWorldPhysicsTestData.Catalogs();
        var forward = TileWorldPhysicsTestData.SlopedWorld(new(0, 0), new(1, 0));
        var reverse = TileWorldPhysicsTestData.SlopedWorld(new(1, 0), new(0, 0));
        reverse.DisplayName = "Renamed";
        var a = TileWorldColliders.Build(forward, catalogs);
        var b = TileWorldColliders.Build(reverse, catalogs);
        Assert.Equal(a.Hash, b.Hash);
        Assert.Equal(a.Medium.SemanticIdentity, b.Medium.SemanticIdentity);
        float height = a.MovementGroundHeight(5.5f, -5.5f);
        var normal = a.MovementGroundNormal(5.5f, -5.5f);
        for (int z = 5; z <= 6; z++)
            for (int x = 5; x <= 6; x++) forward.SetCornerHeightCm(x, z, 0, 1000);
        Assert.Equal(height, a.MovementGroundHeight(5.5f, -5.5f));
        Assert.Equal(normal, a.MovementGroundNormal(5.5f, -5.5f));
        Assert.NotEqual(height, a.Ground.HeightAt(5.5f, -5.5f));
        using var physicsA = new BepuPhysicsWorld(Vector3.Zero);
        using var physicsB = new BepuPhysicsWorld(Vector3.Zero);
        using var installedA = TileWorldColliders.Build(reverse, catalogs, "first").AddTo(physicsA);
        using var installedB = TileWorldColliders.Build(reverse, catalogs, "second").AddTo(physicsB);
        var registrationA = installedA.CreateMovementRegistration("first");
        var registrationB = installedB.CreateMovementRegistration("second");
        using var leaseB = physicsB.AcquireQueryReadLease();
        Assert.Equal(MovementAvailability.Invalid, registrationA.Authenticate(leaseB));
        Assert.Equal(MovementAvailability.Known, registrationB.Authenticate(leaseB));
    }

    [Fact]
    public void NonResidentHoleDeclarationIsAcceptedAndOnlyMissingAnalyticQueriesRefuse()
    {
        var doc = TileWorldPhysicsTestData.FlatWorld(new(0, 0), new(2, 0));
        var colliders = TileWorldColliders.Build(doc, TileWorldPhysicsTestData.Catalogs(), "islands");
        using var physics = new BepuPhysicsWorld(Vector3.Zero);
        using var installed = colliders.AddTo(physics);
        var registration = installed.CreateMovementRegistration("islands");
        Assert.True(float.IsNaN(colliders.MovementGroundHeight(96, -20)));
        Assert.Equal(0, colliders.MovementGroundHeight(20, -20));
        Assert.Equal(0, colliders.MovementGroundHeight(150, -20));
        var tuning = OrdinaryTileWaterTests.Tuning(WaterTraversalMode.DryOnly);
        using var binding = EffectiveMovementBinding.Ordinary(tuning, physics, registration,
            colliders.MovementGroundHeight, colliders.MovementGroundNormal,
            MovementBoundary.Rectangle(0, -64, 192, 0), colliders.Medium);
        var frame = new MovementFrameDescriptor(default, Vector3.Zero, 1);
        Assert.Equal(MovementAvailability.Known, binding.TryAcquire(binding.CreateOrdinaryScope(frame), out var lease));
        using (lease)
        {
            var inside = new FramedMovementState(new MoveState { Position = new(20, 0.75f, -20), Grounded = true }, frame, null);
            var advanced = binding.StepTowards(inside, Vector2.UnitX, false, 1f / 30, lease!);
            Assert.Equal(MovementStepOutcome.Advanced, advanced.Outcome);
            Assert.True(advanced.State.State.Position.X > 20);
            var edge = new FramedMovementState(new MoveState { Position = new(63.99f, 0.75f, -20), Grounded = true }, frame, null);
            var held = binding.StepTowards(edge, Vector2.UnitX, false, 1f / 30, lease!);
            Assert.NotEqual(MovementStepOutcome.Advanced, held.Outcome);
            Assert.Equal(edge.State.Position, held.State.State.Position);
        }
    }
}
