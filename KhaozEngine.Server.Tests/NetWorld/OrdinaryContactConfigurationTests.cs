using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Physics;
using KhaozEngine.Tests.NetWorld.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public partial class OrdinaryContactConfigurationTests
{
    [Theory]
    [InlineData("registration")]
    [InlineData("boundary")]
    [InlineData("point-boundary")]
    [InlineData("medium")]
    [InlineData("two-selections")]
    [InlineData("gravity")]
    [InlineData("fall")]
    [InlineData("swim")]
    [InlineData("legacy-hop")]
    public void NetworkConstructorsRefuseBeforeFirstTick(string refusal)
    {
        using var world = new OrdinaryPlayerWorld();
        using var explicitWorld = new ExplicitPlayerEnvironment();
        var tuning = refusal switch
        {
            "gravity" => OrdinaryPlayerWorld.Tuning with { Gravity = 0 },
            "fall" => OrdinaryPlayerWorld.Tuning with { MaxFallSpeed = 0 },
            "swim" => OrdinaryPlayerWorld.Tuning with { SwimEnterDepthFraction = 0 },
            "legacy-hop" => MoveTuning.Default with { Water = new(WaterTraversalMode.Legacy, 1) },
            _ => OrdinaryPlayerWorld.Tuning
        };
        var registration = refusal == "registration" ? null : world.Registration;
        var boundary = refusal is "boundary" or "point-boundary" ? null : world.Boundary;
        WorldBounds? bounds = refusal == "point-boundary" ? new PointBounds() : null;
        Func<float, float, float, MovementMedium>? medium = refusal == "medium" ? (_, _, _) => MovementMedium.Dry : null;
        var settings = refusal == "two-selections" ? new ExplicitPlayerMovement(explicitWorld.Context,
            explicitWorld.Scope, new(WaterTraversalMode.SurfaceSwimmer, 7)) : null;
        Assert.Throws<ArgumentException>(() => new PlayerMoveSimulator(world.Ground, tuning, null, bounds,
            world.Physics, medium, SamplerSpace.World, settings, null, registration, boundary));
        Assert.Throws<ArgumentException>(() => new PlayerMovementSystem(world.Ground, tuning, null, bounds,
            world.Physics, medium, default, SamplerSpace.World, settings, null, registration, boundary));
        var (st, ct) = LoopbackTransport.CreatePair();
        using (st) using (ct)
        {
            Assert.Throws<ArgumentException>(() => new WorldServer(st, new()
            {
                FrameAnchoring = false, MovementRegistration = registration, MovementBoundary = boundary, ExplicitMovement = settings
            }, world.Ground, tuning, bounds: bounds, physics: world.Physics, medium: medium));
            Assert.Throws<ArgumentException>(() => new WorldClient(ct, world.Ground, tuning, new()
            {
                FrameAnchoring = false, MovementRegistration = registration, MovementBoundary = boundary, ExplicitMovement = settings
            }, bounds: bounds, physics: world.Physics, medium: medium));
        }
    }

    [Fact]
    public void BoundsCertificateCanSupplyTheOrdinaryBoundary()
    {
        using var world = new OrdinaryPlayerWorld();
        using var simulator = new PlayerMoveSimulator(world.Ground, OrdinaryPlayerWorld.Tuning, null,
            new RectBounds(-10, -10, 10, 10), world.Physics, null, SamplerSpace.World, null, null, world.Registration, null);
        using var read = simulator.BeginExplicitRead(world.Initial());
        Assert.True(read!.BasisValid);
    }

    [Fact]
    public void MissingCellFactoriesAndTwoSelectionsRefuseBeforeCellsExist()
    {
        var (st, ct) = LoopbackTransport.CreatePair();
        using (st) using (ct)
        {
            int allocations = 0;
            Assert.Throws<ArgumentException>(() => new ShardedWorldServer(st, new()
            { PhysicsWorldFactory = _ => { allocations++; throw new InvalidOperationException(); } },
                (_, _) => 0, OrdinaryPlayerWorld.Tuning));
            Assert.Equal(0, allocations);
            Assert.Throws<ArgumentException>(() => new ShardedWorldServer(st, new()
            {
                MovementRegistrationFactory = _ => throw new InvalidOperationException(),
                MovementBoundaryFactory = _ => MovementBoundary.Rectangle(-20, -20, 20, 20),
                ExplicitMovementFactory = _ => throw new InvalidOperationException()
            }, (_, _) => 0, OrdinaryPlayerWorld.Tuning));
        }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryHeadRefusesUnselectablePhysicsOrMissingCertifiedSweep(bool selectable)
    {
        using var world = new ObservedPhysicsWorld { Selectable = selectable, CertifiedSweep = false };
        OrdinaryMovementRegistration registration;
        using (var lease = world.AcquireQueryReadLease())
            registration = OrdinaryMovementRegistration.CreateBuilder("world", world,
                new(new(-20, -8, -20), new(20, 8, 20), 0, 0)).Build(lease);
        var boundary = MovementBoundary.Rectangle(-20, -20, 20, 20);
        Assert.Throws<NotSupportedException>(() => new PlayerMoveSimulator((_, _) => 0, OrdinaryPlayerWorld.Tuning,
            null, null, world, null, SamplerSpace.World, null, null, registration, boundary));
        Assert.Throws<NotSupportedException>(() => new PlayerMovementSystem((_, _) => 0, OrdinaryPlayerWorld.Tuning,
            null, null, world, null, default, SamplerSpace.World, null, null, registration, boundary));
        var (st, ct) = LoopbackTransport.CreatePair();
        using (st) using (ct)
        {
            Assert.Throws<NotSupportedException>(() => new WorldServer(st, new()
            { FrameAnchoring = false, MovementRegistration = registration, MovementBoundary = boundary },
                (_, _) => 0, OrdinaryPlayerWorld.Tuning, physics: world));
            Assert.Throws<NotSupportedException>(() => new WorldClient(ct, (_, _) => 0, OrdinaryPlayerWorld.Tuning, new()
            { FrameAnchoring = false, MovementRegistration = registration, MovementBoundary = boundary }, physics: world));
        }
        Assert.Equal(selectable ? 4 : 0, world.ViewDisposals);
        Assert.Equal(0, world.SourceDisposals);
    }

    [Fact]
    public void PointOnlyWorldBoundsIsRefusedAtConfiguration()
    {
        using var world = new OrdinaryPlayerWorld();
        Assert.Throws<ArgumentException>(() => new PlayerMoveSimulator(world.Ground, OrdinaryPlayerWorld.Tuning,
            null, new PointBounds(), world.Physics, null, SamplerSpace.World, null, null, world.Registration, null));
    }


    [Theory]
    [InlineData("registration")]
    [InlineData("boundary")]
    [InlineData("view")]
    public void BadReturnedCellConfigurationRefusesBeforeWiringOrPublicCellNotification(string row)
    {
        var (st, ct) = LoopbackTransport.CreatePair();
        using (st) using (ct)
        using (var source = new ObservedPhysicsWorld { CertifiedSweep = row != "view" })
        using (var server = new ShardedWorldServer(st, new()
        {
            FrameAnchoring = false, PhysicsWorldFactory = _ => source,
            MovementRegistrationFactory = cell => row == "registration" ? null! : OrdinaryContactLifecycleTests.Register(cell.Physics!),
            MovementBoundaryFactory = _ => row == "boundary" ? null! : MovementBoundary.Rectangle(-20, -20, 20, 20)
        }, (_, _) => 0, OrdinaryPlayerWorld.Tuning))
        {
            int notifications = 0;
            server.CellCreated += _ => notifications++;
            if (row == "view") Assert.Throws<NotSupportedException>(() => server.Host.EnsureCell(new(0, 0)));
            else Assert.Throws<InvalidOperationException>(() => server.Host.EnsureCell(new(0, 0)));
            Assert.Equal(0, notifications);
            if (server.Host.TryGetCell(new(0, 0), out var cell)) Assert.Null(cell.ImportAdmission);
            Assert.Equal(row == "view" ? 1 : 0, source.ViewDisposals);
        }
    }

    [Theory]
    [InlineData("gravity")]
    [InlineData("fall")]
    [InlineData("swim")]
    public void ShardedTuningRefusesBeforeAllocatingACell(string row)
    {
        var tuning = row switch
        {
            "gravity" => OrdinaryPlayerWorld.Tuning with { Gravity = 0 },
            "fall" => OrdinaryPlayerWorld.Tuning with { MaxFallSpeed = 0 },
            _ => OrdinaryPlayerWorld.Tuning with { SwimEnterDepthFraction = 0 }
        };
        var (st, ct) = LoopbackTransport.CreatePair();
        using (st) using (ct)
        {
            int allocations = 0;
            Assert.Throws<ArgumentException>(() => new ShardedWorldServer(st, new()
            {
                PhysicsWorldFactory = _ => { allocations++; throw new InvalidOperationException(); },
                MovementRegistrationFactory = _ => throw new InvalidOperationException(),
                MovementBoundaryFactory = _ => MovementBoundary.Rectangle(-20, -20, 20, 20)
            }, (_, _) => 0, tuning));
            Assert.Equal(0, allocations);
        }
    }

    sealed class PointBounds : WorldBounds
    {
        public override bool Contains(float x, float z) => true;
        public override Vector2 Clamp(float x, float z) => new(x, z);
    }

}
