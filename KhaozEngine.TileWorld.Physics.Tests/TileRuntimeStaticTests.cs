using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.TileWorld.Physics;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Physics;

public class TileRuntimeStaticTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExistingViewsSeeUnbakedWallWithoutChangingTileOwnershipOrDocument(bool contact)
    {
        var document = TileWorldPhysicsTestData.FlatWorld();
        var catalogs = TileWorldPhysicsTestData.Catalogs();
        var colliders = TileWorldColliders.Build(document, catalogs, "tiles");
        var hash = colliders.Hash;
        using var physics = new BepuPhysicsWorld(Vector3.Zero);
        using var installed = colliders.AddTo(physics);
        var handles = installed.Handles.ToArray();
        var grounds = installed.GroundHandles.ToArray();
        var indices = installed.ColliderIndices.ToArray();
        using var legacyView = installed.CreateMovementQueryView();
        OrdinaryMovementRegistration movement;
        var extent = new OrdinaryMovementExtent(Vector3.Min(colliders.MovementExtent.Minimum, new(0, -4, 0)),
            Vector3.Max(colliders.MovementExtent.Maximum, new(20, 4, 20)), 0, 0);
        using (var read = physics.AcquireQueryReadLease())
            movement = installed.CreateMovementRegistrationBuilder("tiles", extent).Build(read);
        var tuning = MoveTuning.Default with
        {
            Controller = MovementControllerKind.Contact,
            CapsuleRadius = 0.25f,
            CapsuleHalfHeight = 0.75f,
            WalkSpeed = 4,
            Water = new(WaterTraversalMode.DryOnly)
        };
        var boundary = MovementBoundary.Rectangle(0, 0, 64, 64);
        using var binding = EffectiveMovementBinding.Ordinary(tuning, physics, movement,
            colliders.MovementGroundHeight, colliders.MovementGroundNormal, boundary);
        using var context = new GroundMoveContext(colliders.MovementGroundHeight, colliders.MovementGroundNormal,
            physics, null, null, legacyView, null, movement, boundary);
        movement.ApplyRuntimeStatics([RuntimeStaticChange.Set(new("tiles", "runtime/wall"),
            new BoxShape(new(0.2f, 1, 3)), Pose.At(new(10, 1, 10)))]);
        Assert.True(legacyView.Raycast(new(5, 1, 10), Vector3.UnitX, 10, out var hit));
        using (var read = physics.AcquireQueryReadLease())
        {
            Assert.Equal(MovementAvailability.Known, movement.TryBind(read, hit.Body!.Value, 0, out var owner));
            Assert.Equal(new MovementSupportKey("tiles", "runtime/wall"), owner);
            foreach (StaticHandle ground in grounds)
                Assert.Equal(MovementAvailability.Unresolved, movement.TryBind(read, ground, 0, out _));
        }
        var body = new MoveState { Position = new(5, 0.75f, 10), Grounded = true };
        if (!contact) tuning = tuning with { Controller = MovementControllerKind.Legacy, Water = default };
        for (int tick = 0; tick < 50; tick++)
            body = context.Step(body, Vector2.UnitX, false, 1f / 30, tuning);
        Assert.InRange(body.Position.X, 9.5f, 9.8f - tuning.CapsuleRadius - (contact ? 0.0005f : 0.005f));
        movement.ApplyRuntimeStatics([RuntimeStaticChange.Remove(new("tiles", "runtime/wall"))]);
        for (int tick = 0; tick < 20; tick++)
            body = context.Step(body, Vector2.UnitX, false, 1f / 30, tuning);
        Assert.True(body.Position.X > 11);
        Assert.Equal(handles, installed.Handles);
        Assert.Equal(grounds, installed.GroundHandles);
        Assert.Equal(indices, installed.ColliderIndices);
        Assert.Equal(grounds, movement.ExcludedStatics);
        Assert.Equal(hash, TileWorldColliders.Build(document, catalogs, "tiles").Hash);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RuntimeMeshAndCompoundHaveIndependentRemovalAndWorldLifetime(bool mesh)
    {
        var colliders = TileWorldColliders.Build(TileWorldPhysicsTestData.FlatWorld(), TileWorldPhysicsTestData.Catalogs(), "tiles");
        using var physics = new BepuPhysicsWorld(Vector3.Zero);
        using var installed = colliders.AddTo(physics);
        var extent = new OrdinaryMovementExtent(Vector3.Min(colliders.MovementExtent.Minimum, new(-4)),
            Vector3.Max(colliders.MovementExtent.Maximum, new(68, 4, 68)), 0, 0);
        OrdinaryMovementRegistration movement;
        using (var read = physics.AcquireQueryReadLease()) movement = installed.CreateMovementRegistrationBuilder("tiles", extent).Build(read);
        using var view = installed.CreateMovementQueryView();
        PhysicsShape shape = mesh
            ? new TriangleMeshShape([new(0, -1, -1), new(0, 1, -1), new(0, 0, 1)], [0, 1, 2])
            : new CompoundShape([new(new BoxShape(Vector3.One), Pose.Identity)]);
        var owner = new MovementSupportKey("tiles", "runtime/piece");
        var request = RuntimeStaticChange.Set(owner, shape, Pose.At(new(10, 1, 10)));
        movement.ApplyRuntimeStatics([request]);
        Assert.True(view.Raycast(new(5, 1, 10), Vector3.UnitX, 10, out _));
        movement.ApplyRuntimeStatics([RuntimeStaticChange.Remove(owner)]);
        Assert.False(view.Raycast(new(5, 1, 10), Vector3.UnitX, 10, out _));
        movement.ApplyRuntimeStatics([request]);
        installed.Remove();
        // Baked removal owns only baked handles. The remaining runtime shape belongs to the complete world.
        Assert.True(view.Raycast(new(5, 1, 10), Vector3.UnitX, 10, out _));
        view.Dispose();
        physics.Dispose();
        Assert.Throws<ObjectDisposedException>(() => physics.AcquireQueryReadLease());
    }

    [Fact]
    public void FlatTileExtentDoesNotAutomaticallyGrowToAdmitATallRuntimeWall()
    {
        var colliders = TileWorldColliders.Build(TileWorldPhysicsTestData.FlatWorld(), TileWorldPhysicsTestData.Catalogs(), "tiles");
        using var physics = new BepuPhysicsWorld(Vector3.Zero);
        using var installed = colliders.AddTo(physics);
        var movement = installed.CreateMovementRegistration("tiles");
        long before = physics.StaticGeometryGeneration;
        Assert.Throws<ArgumentException>(() => movement.ApplyRuntimeStatics([RuntimeStaticChange.Set(new("tiles", "wall"),
            new BoxShape(new(0.2f, 1, 3)), Pose.At(new(10, 1, 10)))]));
        Assert.Equal(before, physics.StaticGeometryGeneration);
        Assert.Equal(colliders.MovementExtent, movement.Extent);
    }
}
