using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class RuntimeStaticDeterminismTests
{
    [Theory]
    [InlineData(false, 0f)]
    [InlineData(true, 0f)]
    [InlineData(false, 8f)]
    [InlineData(true, 8f)]
    public void OppositeBatchEnumerationPreservesEveryMovementBitAndCanonicalSupport(bool contact, float offset)
    {
        Vector3 origin = new(offset, 2, -16);
        using var a = new RuntimeStaticScene(contact, origin);
        using var b = new RuntimeStaticScene(contact, origin);
        var left = new MoveState { Position = origin + new Vector3(0, 1.25f, 0), Grounded = true };
        var right = left;
        bool runtimeSupport = false;
        bool blockedAtCorner = false;
        for (int tick = 0; tick < 90; tick++)
        {
            RuntimeStaticChange[] batch = Changes(tick, origin);
            a.Scene.Registration.ApplyRuntimeStatics(batch);
            Array.Reverse(batch);
            b.Scene.Registration.ApplyRuntimeStatics(batch);
            Vector2 command = tick < 40 ? Vector2.Normalize(Vector2.One) : Vector2.UnitX;
            var first = a.Tick(left, command);
            var second = b.Tick(right, command);
            RuntimeStaticScene.AssertBits(first.State, second.State);
            Assert.Equal(first.Outcome, second.Outcome);
            Assert.Equal(first.Support, second.Support);
            runtimeSupport |= first.Support?.LocalId == "alpha-platform";
            if (tick == 32)
            {
                Assert.True(first.State.Position.X > origin.X + 1);
                Assert.True(first.State.Position.Z > origin.Z + 1);
                blockedAtCorner = first.State.Position.X < origin.X + 3 && first.State.Position.Z < origin.Z + 3;
            }
            left = first.State;
            right = second.State;
        }
        Assert.True(blockedAtCorner);
        if (contact) Assert.True(runtimeSupport);
        // Pre-existing selected provenance and analytic exclusions survive the complete timeline.
        using var read = a.Scene.Physics.AcquireQueryReadLease();
        Assert.True(a.Scene.Physics.Raycast(new(-10, 5, -10), -Vector3.UnitY, 6, out var hit));
        Assert.Equal(MovementAvailability.Known, a.Scene.Registration.TryBind(read, hit.Body!.Value, 0, out var owner));
        Assert.Equal(new MovementSupportKey("world", "untouched"), owner);
        Assert.Single(a.Scene.Registration.ExcludedStatics);
    }

    static RuntimeStaticChange[] Changes(int tick, Vector3 origin)
    {
        RuntimeStaticChange Set(string owner, Vector3 half, Vector3 centre) => RuntimeStaticChange.Set(
            new("world", owner), new BoxShape(half), Pose.At(origin + centre));
        RuntimeStaticChange Remove(string owner) => RuntimeStaticChange.Remove(new("world", owner));
        return tick switch
        {
            0 => [Set("z-wall", new(4, 2, 0.2f), new(0, 2, 3)), Set("x-wall", new(0.2f, 2, 4), new(3, 2, 0)),
                Set("omega-platform", new(2, 0.25f, 2), new(0, 0.25f, 0)),
                Set("alpha-platform", new(2, 0.25f, 2), new(0, 0.25f, 0))],
            36 => [Set("x-wall", new(0.2f, 2, 4), new(4, 2, 0)), Remove("z-wall")],
            48 => [Remove("alpha-platform"), Remove("omega-platform"), Remove("x-wall")],
            60 => [Set("z-wall", new(4, 2, 0.2f), new(0, 2, 3)), Set("x-wall", new(0.2f, 2, 4), new(8, 2, 0))],
            72 => [Remove("x-wall"), Remove("z-wall")],
            _ => []
        };
    }
}
