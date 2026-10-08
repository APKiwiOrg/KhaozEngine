using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public partial class ExplicitPlayerHeadsTests
{
    [Fact]
    public void UnresolvedTeleportPreservesTheWholeAuthoritativeStateAndEpoch()
    {
        using var pair = new Pair();
        pair.Pump(6);
        Assert.True(pair.Server.TryGetPlayerState(0, out var before));
        var target = before;
        target.Position = new(2, 0.751f, 0);
        target.Move.WaterExcursion = WaterExcursionState.AirborneFromWater;
        target.TeleportEpoch = 200;
        pair.ServerEnvironment.Availability = MovementAvailability.Unresolved;
        pair.Server.SetPlayerState(0, target, teleport: true);
        Assert.True(pair.Server.TryGetPlayerState(0, out var held));
        Assert.Equal(before, held);
    }

    [Theory]
    [InlineData(0.751f, true)]
    [InlineData(2f, false)]
    public void TeleportPublishesOnlyAClassifiedDestinationAndClearsTheWaterArc(float y, bool grounded)
    {
        using var pair = new Pair();
        pair.Pump(6);
        Assert.True(pair.Server.TryGetPlayerState(0, out var before));
        var target = before;
        target.Position = new(2, y, 0);
        target.Move.WaterExcursion = WaterExcursionState.AirborneFromWater;
        target.Move.Swimming = true;
        target.Move.VerticalVelocity = 5;
        target.Move.JumpBufferRemaining = 0.2f;
        int publications = 0;
        pair.ServerEnvironment.OnPinDispose = () =>
        {
            Assert.True(pair.Server.TryGetPlayerState(0, out var published));
            Assert.Equal(2f, published.Position.X);
            Assert.Equal(before.TeleportEpoch + 1, published.TeleportEpoch);
            Assert.Throws<InvalidOperationException>(() => pair.ServerEnvironment.Physics.Step(0.1f));
            publications++;
        };
        pair.Server.SetPlayerState(0, target, teleport: true);
        Assert.True(publications > 0);
        Assert.True(pair.Server.TryGetPlayerState(0, out var after));
        Assert.Equal(grounded, after.Grounded);
        Assert.Equal(y, after.Position.Y);
        Assert.Equal(WaterExcursionState.None, after.Move.WaterExcursion);
        Assert.False(after.Swimming);
        Assert.Equal(0, after.VerticalVelocity);
        Assert.Equal(0, after.Move.JumpBufferRemaining);
        pair.ServerEnvironment.OnPinDispose = null;
    }

    [Fact]
    public void ConfiguredSpawnRefusesUnavailableDataAndUsesExplicitQueriesWhenReady()
    {
        using var pair = new Pair();
        pair.Pump(6);
        pair.ServerEnvironment.Availability = MovementAvailability.Unresolved;
        Assert.False(pair.Server.TryGetConfiguredSpawn(0, out var refused));
        Assert.Equal(default, refused);
        pair.ServerEnvironment.Availability = MovementAvailability.Known;
        Assert.True(pair.Server.TryGetConfiguredSpawn(0, out var ready));
        Assert.True(ready.Grounded);
        Assert.InRange(ready.Position.Y, 0.7509f, 0.7511f);
        Assert.Equal(0, pair.LegacyCalls);
    }
}
