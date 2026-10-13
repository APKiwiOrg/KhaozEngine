using System;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Tests.NetWorld.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public partial class OrdinaryContactConfigurationTests
{
    [Theory]
    [InlineData("physics")]
    [InlineData("registration")]
    [InlineData("boundary")]
    [InlineData("medium")]
    [InlineData("legacy-hop")]
    [InlineData("two-selections")]
    public void EachRequiredCellFactoryAndSelectionRefusesAtTheShardedConstructor(string row)
    {
        var (st, ct) = LoopbackTransport.CreatePair();
        using (st) using (ct)
        {
            int callbacks = 0;
            var config = new ShardedWorldServerConfig
            {
                PhysicsWorldFactory = row == "physics" ? null : _ => { callbacks++; throw new InvalidOperationException(); },
                MovementRegistrationFactory = row == "registration" ? null : _ => { callbacks++; throw new InvalidOperationException(); },
                MovementBoundaryFactory = row == "boundary" ? null : _ => { callbacks++; throw new InvalidOperationException(); },
                ExplicitMovementFactory = row == "two-selections" ? _ => { callbacks++; throw new InvalidOperationException(); } : null
            };
            var tuning = row == "legacy-hop" ? MoveTuning.Default with { Water = new(WaterTraversalMode.Legacy, 1) }
                : OrdinaryPlayerWorld.Tuning;
            Assert.Throws<ArgumentException>(() => new ShardedWorldServer(st, config, (_, _) => 0, tuning,
                medium: row == "medium" ? (_, _, _) => MovementMedium.Dry : null));
            Assert.Equal(0, callbacks);
        }
    }

    [Theory]
    [InlineData("gravity")]
    [InlineData("fall")]
    [InlineData("swim")]
    public void PublicExplicitCellFactoryTuningRefusesBeforeCellAllocation(string row)
    {
        var tuning = row switch
        {
            "gravity" => ExplicitPlayerEnvironment.Tuning with { Gravity = 0 },
            "fall" => ExplicitPlayerEnvironment.Tuning with { MaxFallSpeed = 0 },
            _ => ExplicitPlayerEnvironment.Tuning with { SwimEnterDepthFraction = 0 }
        };
        var (st, ct) = LoopbackTransport.CreatePair();
        using (st) using (ct)
        {
            int callbacks = 0;
            Assert.Throws<ArgumentException>(() => new ShardedWorldServer(st, new()
            {
                ExplicitMovementFactory = _ => { callbacks++; throw new InvalidOperationException(); }
            }, (_, _) => 0, tuning));
            Assert.Equal(0, callbacks);
        }
    }

    [Fact]
    public void ACellRegistrationFromAnotherPhysicsSourceIsRefusedBeforePublication()
    {
        using var wrong = new OrdinaryPlayerWorld();
        var (st, ct) = LoopbackTransport.CreatePair();
        using (st) using (ct)
        using (var source = new ObservedPhysicsWorld())
        using (var server = new ShardedWorldServer(st, new()
        {
            FrameAnchoring = false,
            PhysicsWorldFactory = _ => source,
            MovementRegistrationFactory = _ => wrong.Registration,
            MovementBoundaryFactory = _ => MovementBoundary.Rectangle(-20, -20, 20, 20)
        }, (_, _) => 0, OrdinaryPlayerWorld.Tuning))
        {
            int notifications = 0;
            server.CellCreated += _ => notifications++;
            Assert.Throws<ArgumentException>(() => server.Host.EnsureCell(new(0, 0)));
            Assert.Equal(0, notifications);
            Assert.Equal(0, source.ViewCreations);
            if (server.Host.TryGetCell(new(0, 0), out var cell)) Assert.Null(cell.ImportAdmission);
        }
    }
}
