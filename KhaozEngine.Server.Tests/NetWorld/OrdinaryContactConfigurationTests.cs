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
    [InlineData("physics")]
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
        Check(() => new PlayerMoveSimulator(world.Ground, tuning, null, bounds,
            refusal == "physics" ? null : world.Physics, medium, SamplerSpace.World, settings, null, registration, boundary));
        Check(() => new PlayerMovementSystem(world.Ground, tuning, null, bounds,
            refusal == "physics" ? null : world.Physics, medium, default, SamplerSpace.World, settings, null, registration, boundary));
        var (st, ct) = LoopbackTransport.CreatePair();
        using (st) using (ct)
        {
            Check(() => new WorldServer(st, new()
            {
                FrameAnchoring = false,
                MovementRegistration = registration,
                MovementBoundary = boundary,
                ExplicitMovement = settings
            }, world.Ground, tuning, bounds: bounds, physics: refusal == "physics" ? null : world.Physics, medium: medium));
            Check(() => new WorldClient(ct, world.Ground, tuning, new()
            {
                FrameAnchoring = false,
                MovementRegistration = registration,
                MovementBoundary = boundary,
                ExplicitMovement = settings
            }, bounds: bounds, physics: refusal == "physics" ? null : world.Physics, medium: medium));
        }
        using var repaired = new PlayerMoveSimulator(world.Ground, OrdinaryPlayerWorld.Tuning, null, null,
            world.Physics, null, SamplerSpace.World, null, null, world.Registration, world.Boundary);
        void Check(Action action)
        {
            var failure = Assert.Throws<MovementConfigurationException>(action);
            Assert.Equal(refusal switch
            {
                "physics" => MovementConfigurationRefusal.PhysicsRequired,
                "registration" => MovementConfigurationRefusal.RegistrationRequired,
                "boundary" or "point-boundary" => MovementConfigurationRefusal.BoundaryRequired,
                "two-selections" => MovementConfigurationRefusal.ConflictingMovementSelections,
                "medium" => MovementConfigurationRefusal.WaterFieldRequired,
                "legacy-hop" => MovementConfigurationRefusal.LegacyWaterNondefault,
                _ => MovementConfigurationRefusal.KernelPositiveValueRequired
            }, failure.Reason);
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
            Assert.Throws<MovementConfigurationException>(() => new ShardedWorldServer(st, new()
            { PhysicsWorldFactory = _ => { allocations++; throw new InvalidOperationException(); } },
                (_, _) => 0, OrdinaryPlayerWorld.Tuning));
            Assert.Equal(0, allocations);
            Assert.Throws<MovementConfigurationException>(() => new ShardedWorldServer(st, new()
            {
                MovementRegistrationFactory = _ => throw new InvalidOperationException(),
                MovementBoundaryFactory = _ => MovementBoundary.Rectangle(-20, -20, 20, 20),
                ExplicitMovementFactory = _ => throw new InvalidOperationException()
            }, (_, _) => 0, OrdinaryPlayerWorld.Tuning));
        }
    }
    [Theory]
    [InlineData(false, typeof(NotSupportedException))]
    [InlineData(true, typeof(MovementCapabilityException))]
    public void EveryHeadRefusesUnselectablePhysicsOrMissingCertifiedSweep(bool selectable, Type expectedType)
    {
        using var world = new ObservedPhysicsWorld { Selectable = selectable, CertifiedSweep = false };
        OrdinaryMovementRegistration registration;
        using (var lease = world.AcquireQueryReadLease())
            registration = OrdinaryMovementRegistration.CreateBuilder("world", world,
                new(new(-20, -8, -20), new(20, 8, 20), 0, 0)).Build(lease);
        var boundary = MovementBoundary.Rectangle(-20, -20, 20, 20);
        Check(() => new PlayerMoveSimulator((_, _) => 0, OrdinaryPlayerWorld.Tuning,
            null, null, world, null, SamplerSpace.World, null, null, registration, boundary));
        Check(() => new PlayerMovementSystem((_, _) => 0, OrdinaryPlayerWorld.Tuning,
            null, null, world, null, default, SamplerSpace.World, null, null, registration, boundary));
        var (st, ct) = LoopbackTransport.CreatePair();
        using (st) using (ct)
        {
            Check(() => new WorldServer(st, new()
            { FrameAnchoring = false, MovementRegistration = registration, MovementBoundary = boundary },
                (_, _) => 0, OrdinaryPlayerWorld.Tuning, physics: world));
            Check(() => new WorldClient(ct, (_, _) => 0, OrdinaryPlayerWorld.Tuning, new()
            { FrameAnchoring = false, MovementRegistration = registration, MovementBoundary = boundary }, physics: world));
        }
        Assert.Equal(selectable ? 4 : 0, world.ViewDisposals);
        Assert.Equal(0, world.SourceDisposals);
        void Check(Action action)
        {
            var failure = Assert.Throws(expectedType, action);
            if (failure is MovementCapabilityException capability)
                Assert.Equal(MovementConfigurationRefusal.CapsuleSweepRequired, capability.Reason);
        }
    }

    [Fact]
    public void PointOnlyWorldBoundsIsRefusedAtConfiguration()
    {
        using var world = new OrdinaryPlayerWorld();
        Assert.Throws<MovementConfigurationException>(() => new PlayerMoveSimulator(world.Ground, OrdinaryPlayerWorld.Tuning,
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
            FrameAnchoring = false,
            PhysicsWorldFactory = _ => source,
            MovementRegistrationFactory = cell => row == "registration" ? null! : OrdinaryContactLifecycleTests.Register(cell.Physics!),
            MovementBoundaryFactory = _ => row == "boundary" ? null! : MovementBoundary.Rectangle(-20, -20, 20, 20)
        }, (_, _) => 0, OrdinaryPlayerWorld.Tuning))
        {
            int notifications = 0;
            server.CellCreated += _ => notifications++;
            if (row == "view") Assert.Equal(MovementConfigurationRefusal.CapsuleSweepRequired,
                Assert.Throws<MovementCapabilityException>(() => server.Host.EnsureCell(new(0, 0))).Reason);
            else Assert.Throws<InvalidOperationException>(() => server.Host.EnsureCell(new(0, 0)));
            Assert.Equal(0, notifications);
            Assert.False(server.Host.TryGetCell(new(0, 0), out _));
            Assert.Equal(row == "view" ? 1 : 0, source.ViewDisposals);
            Assert.Equal(1, source.SourceDisposals);
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
            Assert.Throws<MovementConfigurationException>(() => new ShardedWorldServer(st, new()
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
