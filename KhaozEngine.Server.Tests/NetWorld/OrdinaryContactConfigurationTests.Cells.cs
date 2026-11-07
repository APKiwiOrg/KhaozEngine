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
    [InlineData("medium-with-bounds")]
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
                MovementBoundaryFactory = row is "boundary" or "medium-with-bounds" ? null : _ => { callbacks++; throw new InvalidOperationException(); },
                ExplicitMovementFactory = row == "two-selections" ? _ => { callbacks++; throw new InvalidOperationException(); } : null
            };
            var tuning = row == "legacy-hop" ? MoveTuning.Default with { Water = new(WaterTraversalMode.Legacy, 1) }
                : OrdinaryPlayerWorld.Tuning;
            var failure = Assert.Throws<MovementConfigurationException>(() => new ShardedWorldServer(st, config, (_, _) => 0, tuning,
                bounds: row == "medium-with-bounds" ? new RectBounds(-20, -20, 20, 20) : null,
                medium: row is "medium" or "medium-with-bounds" ? (_, _, _) => MovementMedium.Dry : null));
            Assert.Equal(row switch
            {
                "physics" => MovementConfigurationRefusal.PhysicsFactoryRequired,
                "registration" => MovementConfigurationRefusal.RegistrationFactoryRequired,
                "boundary" => MovementConfigurationRefusal.BoundaryFactoryRequired,
                "medium" or "medium-with-bounds" => MovementConfigurationRefusal.WaterFieldFactoryRequired,
                "legacy-hop" => MovementConfigurationRefusal.LegacyWaterNondefault,
                _ => MovementConfigurationRefusal.ConflictingMovementSelections
            }, failure.Reason);
            if (row is "physics" or "registration" or "boundary" or "medium" or "medium-with-bounds")
            {
                Assert.Equal(row switch
                {
                    "physics" => "PhysicsWorldFactory",
                    "registration" => "MovementRegistrationFactory",
                    "boundary" => "MovementBoundaryFactory",
                    _ => "MovementWaterFieldFactory"
                }, failure.MemberName);
                Assert.Equal("config", failure.ParamName);
            }
            Assert.Equal(0, callbacks);
            using var repaired = new ShardedWorldServer(st, new()
            {
                FrameAnchoring = false,
                PhysicsWorldFactory = config.PhysicsWorldFactory ?? (_ => throw new InvalidOperationException()),
                MovementRegistrationFactory = config.MovementRegistrationFactory ?? (_ => throw new InvalidOperationException()),
                MovementBoundaryFactory = row == "medium-with-bounds" ? null
                    : config.MovementBoundaryFactory ?? (_ => MovementBoundary.Rectangle(-20, -20, 20, 20)),
                MovementWaterFieldFactory = _ => null
            }, (_, _) => 0, row == "legacy-hop" ? MoveTuning.Default : OrdinaryPlayerWorld.Tuning,
                bounds: row == "medium-with-bounds" ? new RectBounds(-20, -20, 20, 20) : null,
                medium: row is "medium" or "medium-with-bounds" ? (_, _, _) => MovementMedium.Dry : null);
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
            Assert.Throws<MovementConfigurationException>(() => new ShardedWorldServer(st, new()
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
