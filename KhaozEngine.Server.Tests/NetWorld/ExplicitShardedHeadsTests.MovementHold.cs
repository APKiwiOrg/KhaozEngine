using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public partial class ExplicitShardedHeadsTests
{
    [Fact]
    public void HeldShardedSeatConsumesExplicitWalkAndJumpThenWalksOnRelease()
    {
        using var pair = new Pair();
        pair.Pump(6);
        Assert.True(pair.Client.Joined);
        pair.Server.HoldMovement(PlayerRef.Slot(0));
        pair.Pump(1);
        Assert.True(pair.Server.IsMovementHeld(0));
        Assert.True(pair.Server.TryGetPlayerState(0, out var start));

        for (int i = 0; i < 4; i++)
        {
            Assert.True(pair.Client.SendInput(new(Vector2.UnitX, false, 0, jump: i % 2 == 1)) >= 0);
            pair.Pump(1);
            Assert.True(pair.Server.TryGetPlayerState(0, out var held));
            Assert.Equal(start.Position.X, held.Position.X, 3);
            Assert.Equal(start.Position.Y, held.Position.Y, 3);
            Assert.Equal(start.Position.Z, held.Position.Z, 3);
            Assert.True(held.Grounded);
            Assert.Equal(0, held.VerticalVelocity);
            Assert.Equal(0, pair.Client.PendingPredictionCommands);
        }

        pair.Server.ReleaseMovement(PlayerRef.Slot(0));
        Assert.True(pair.Client.SendInput(new(Vector2.UnitX, false, 0)) >= 0);
        pair.Pump(1);
        Assert.False(pair.Server.IsMovementHeld(0));
        Assert.True(pair.Server.TryGetPlayerState(0, out var released));
        Assert.InRange(released.Position.X - start.Position.X, 0.3999f, 0.4001f);
        Assert.Equal(0, pair.LegacyCalls);
    }
}
