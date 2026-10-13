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
    [InlineData("gravity")]
    [InlineData("fall")]
    [InlineData("swim")]
    [InlineData("foot")]
    [InlineData("selector")]
    public void PublicExplicitShardedPreflightRefusesInvalidTuningBeforeCellAllocation(string row)
    {
        var tuning = row switch
        {
            "gravity" => MoveTuning.Default with { Gravity = 0 },
            "fall" => MoveTuning.Default with { MaxFallSpeed = 0 },
            "swim" => MoveTuning.Default with { SwimEnterDepthFraction = 0 },
            "foot" => MoveTuning.Default with { FootRadiusFraction = 0 },
            _ => MoveTuning.Default with { Controller = (MovementControllerKind)99 }
        };
        var (st, ct) = LoopbackTransport.CreatePair();
        using (st) using (ct)
        {
            int allocations = 0;
            Assert.ThrowsAny<ArgumentException>(() => new ShardedWorldServer(st, new()
            {
                PhysicsWorldFactory = _ => { allocations++; throw new InvalidOperationException(); },
                ExplicitMovementFactory = _ => throw new InvalidOperationException()
            }, (_, _) => 0, tuning));
            Assert.Equal(0, allocations);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PublicExplicitFactoryWaterPolicyIsCheckedAtRealCellBinding(bool conflicting)
    {
        using var environment = new ExplicitPlayerEnvironment();
        var tuning = conflicting ? MoveTuning.Default with { Water = new(WaterTraversalMode.SurfaceSwimmer, 7) } : MoveTuning.Default;
        var water = conflicting ? new WaterTraversalPolicy(WaterTraversalMode.DryOnly) : default;
        var (st, ct) = LoopbackTransport.CreatePair();
        using (st) using (ct)
        {
            int factoryCalls = 0, notifications = 0;
            using var server = new ShardedWorldServer(st, new()
            {
                FrameAnchoring = false,
                ExplicitMovementFactory = _ => { factoryCalls++; return new(environment.Context, environment.Scope, water); }
            }, (_, _) => 0, tuning);
            Assert.Equal(0, factoryCalls);
            server.CellCreated += _ => notifications++;
            Assert.Throws<ArgumentException>(() => server.EnsureCell(new(0, 0)));
            Assert.Equal(1, factoryCalls);
            Assert.Equal(0, notifications);
            Assert.Empty(server.LiveCellCoords);
            Assert.Equal(MovementAvailability.Known, environment.Context.TryAcquire(
                environment.Scope(ExplicitPlayerEnvironment.Initial, default), out var read));
            using (read) read!.AssertCurrent();
        }
    }
}
