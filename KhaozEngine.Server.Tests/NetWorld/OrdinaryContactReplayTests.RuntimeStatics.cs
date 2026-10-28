using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Physics;
using KhaozEngine.Tests.NetWorld.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public partial class OrdinaryContactReplayTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(4096, -8192)]
    public void HostReconstructsHistoricalStaticsBeforeEveryReplayedMovementRead(float x, float z)
    {
        const int ticks = 32;
        var origin = new Vector3(x, 0, z);
        using var server = new OrdinaryPlayerWorld(origin);
        using var authority = server.Simulator();
        var states = new List<PlayerMoveState>();
        var outcomes = new List<MovementStepOutcome>();
        var command = new MoveCommand(Vector2.UnitX, false, 0);
        float startX = x - server.Frame.Anchor.X, startZ = z - server.Frame.Anchor.Z;
        var state = server.Initial(startX, startZ);
        for (int tick = 0; tick < ticks; tick++)
        {
            Apply(server, tick);
            using var read = authority.BeginExplicitRead(state);
            Assert.True(read!.BasisValid);
            state = authority.Step(state, command, OrdinaryPlayerWorld.Dt);
            Assert.True(authority.LastExplicitOutcome is MovementStepOutcome.Advanced or MovementStepOutcome.Blocked,
                $"Tick {tick} returned {authority.LastExplicitOutcome}.");
            states.Add(state);
            outcomes.Add(Assert.IsType<MovementStepOutcome>(authority.LastExplicitOutcome));
        }
        Assert.True(states[3].Position.X > startX + 1);
        Assert.True(states[8].Position.X > states[3].Position.X);
        Assert.InRange(states[17].Position.X, startX + 3, startX + 3.25f);
        SameRuntimeBits(states[16].Move, states[17].Move);
        Assert.True(states[18].Position.X > states[17].Position.X);
        Assert.True(states[^1].Position.X > startX + 7);

        // Reconstruct from the baseline even for a mid-history correction. An inverse diff or today's empty
        // blocker set would lose the installation order and would walk through the historical stop.
        foreach (int firstTick in new[] { 0, 12 })
        {
            using var historical = new OrdinaryPlayerWorld(origin);
            using var replay = historical.Simulator();
            for (int tick = 0; tick < firstTick; tick++) Apply(historical, tick);
            var replayed = firstTick == 0 ? historical.Initial(startX, startZ) : states[firstTick - 1];
            for (int tick = firstTick; tick < ticks; tick++)
            {
                Apply(historical, tick);
                using var read = replay.BeginExplicitRead(replayed);
                Assert.True(read!.BasisValid);
                replayed = replay.Step(replayed, command, OrdinaryPlayerWorld.Dt);
                SameRuntimeBits(states[tick], replayed);
                Assert.Equal(outcomes[tick], replay.LastExplicitOutcome);
            }
        }

        void Apply(OrdinaryPlayerWorld world, int tick)
        {
            var owner = new MovementSupportKey("world", "host-wall");
            if (tick is 4 or 10)
                world.Registration.ApplyRuntimeStatics([RuntimeStaticChange.Set(owner,
                    new BoxShape(new(0.2f, 1, 3)), Pose.At(origin + new Vector3(tick == 4 ? 3 : 3.5f, 1, 0)))]);
            else if (tick == 18)
                world.Registration.ApplyRuntimeStatics([RuntimeStaticChange.Remove(owner)]);
        }
    }

    static void SameRuntimeBits(object? expected, object? actual)
    {
        if (expected is null || actual is null) { Assert.Equal(expected, actual); return; }
        Assert.Equal(expected.GetType(), actual.GetType());
        if (expected is float value)
        {
            Assert.Equal(BitConverter.SingleToInt32Bits(value), BitConverter.SingleToInt32Bits((float)actual));
            return;
        }
        if (expected.GetType().IsPrimitive || expected.GetType().IsEnum || expected is string)
        { Assert.Equal(expected, actual); return; }
        FieldInfo[] fields = expected.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (fields.Length == 0) { Assert.Equal(expected, actual); return; }
        foreach (FieldInfo field in fields) SameRuntimeBits(field.GetValue(expected), field.GetValue(actual));
    }
}
