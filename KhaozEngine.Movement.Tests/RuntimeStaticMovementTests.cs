using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class RuntimeStaticMovementTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RuntimeWallMatchesBakedBlockingSlidesAndRemovalInExistingContexts(bool contact)
    {
        for (int phase = 0; phase < 4; phase++)
        {
            float start = StartPhase(phase);
            using var runtime = new RuntimeStaticScene(contact);
            using var control = new RuntimeStaticScene(contact, install: (physics, builder) =>
            {
                var handle = physics.AddStatic(new BoxShape(new(0.2f, 1, 3)), Pose.At(new(3, 1, 0)));
                builder.Add(handle, 0, int.MaxValue, new("world", "wall"), new(2.8f, 0, -3), new(3.2f, 2, 3));
            });
            runtime.Scene.Registration.ApplyRuntimeStatics([RuntimeStaticRegistrationTests.Wall()]);
            var a = new MoveState { Position = new(start, 0.75f, -1), Grounded = true };
            var b = a;
            for (int tick = 0; tick < 32; tick++)
            {
                a = runtime.Tick(a, Vector2.UnitX).State;
                b = control.Tick(b, Vector2.UnitX).State;
                RuntimeStaticScene.AssertBits(b, a);
            }
            float skin = contact ? ShellMotion.ContactSkin : 0.01f;
            Assert.InRange(a.Position.X, 2.4f, 2.8f - runtime.Tuning.CapsuleRadius - skin * 0.5f);
            float stoppedZ = a.Position.Z;
            for (int tick = 0; tick < 6; tick++) a = runtime.Tick(a, Vector2.Normalize(new(1, 1))).State;
            Assert.True(a.Position.Z > stoppedZ);
            Assert.True(a.Position.X <= 2.8f - runtime.Tuning.CapsuleRadius - skin * 0.5f);
            runtime.Scene.Registration.ApplyRuntimeStatics([RuntimeStaticChange.Remove(new("world", "wall"))]);
            for (int tick = 0; tick < 24; tick++) a = runtime.Tick(a, Vector2.UnitX).State;
            Assert.True(a.Position.X > 5);
            using var known = runtime.Scene.Acquire();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RuntimePlatformSupportsStepsAndFallsAfterRemoval(bool contact)
    {
        for (int phase = 0; phase < 4; phase++)
        {
            using var scene = new RuntimeStaticScene(contact);
            using var baked = new RuntimeStaticScene(contact, install: (physics, builder) =>
            {
                var handle = physics.AddStatic(new BoxShape(new(2, 0.125f, 2)), Pose.At(new(3, 0.125f, 0)));
                builder.Add(handle, 0, int.MaxValue, new("world", "platform"), new(1, 0, -2), new(5, 0.25f, 2));
            });
            var owner = new MovementSupportKey("world", "platform");
            scene.Scene.Registration.ApplyRuntimeStatics([RuntimeStaticChange.Set(owner,
                new BoxShape(new(2, 0.125f, 2)), Pose.At(new(3, 0.125f, 0)))]);
            var body = new MoveState { Position = new(StartPhase(phase), 0.75f, 0), Grounded = true };
            var control = body;
            bool stepped = false;
            MovementSupportKey? bound = null;
            for (int tick = 0; tick < 25; tick++)
            {
                var next = scene.Tick(body, Vector2.UnitX);
                control = baked.Tick(control, Vector2.UnitX).State;
                RuntimeStaticScene.AssertBits(control, next.State);
                stepped |= next.State.StepDeltaY > 0 || next.State.ClimbRate > 0;
                body = next.State;
                bound = next.Support;
            }
            Assert.True(stepped);
            Assert.True(body.Grounded);
            RuntimeStaticScene.AssertBits(control, body);
            if (contact) Assert.Equal(owner, bound);
            // Removal must exceed the controller's ground-stick band to exercise falling rather than snapping.
            scene.Scene.Registration.ApplyRuntimeStatics([RuntimeStaticChange.Set(owner,
                new BoxShape(new(2, 1, 2)), Pose.At(new(3, 1, 0)))]);
            body.Position.Y = 2 + scene.Tuning.CapsuleHalfHeight;
            body = scene.Tick(body, Vector2.Zero).State;
            Assert.True(body.Grounded);
            scene.Scene.Registration.ApplyRuntimeStatics([RuntimeStaticChange.Remove(owner)]);
            bool fell = false;
            for (int tick = 0; tick < 16; tick++)
            {
                body = scene.Tick(body, Vector2.Zero).State;
                fell |= !body.Grounded && body.VerticalVelocity < 0;
            }
            Assert.True(fell);
            Assert.Equal(0.75f, body.Position.Y);
        }
    }

    [Fact]
    public void CeilingLiftOverlapRecoveryAndPlacementUseTheRuntimeSelection()
    {
        using var scene = new OrdinaryContactScene();
        scene.Registration.ApplyRuntimeStatics([RuntimeStaticChange.Set(new("world", "ceiling"),
            new BoxShape(new(3, 0.1f, 3)), Pose.At(new(0, 1.8f, 0)))]);
        using (var queries = scene.Acquire())
        {
            var body = scene.State(new(0, 0.75f, 0));
            var jump = scene.Binding.Step(body, new(Vector2.Zero, false, 0, jump: true), 0.1f, queries);
            Assert.True(jump.State.State.Position.Y <= 1.7f - scene.Tuning.CapsuleHalfHeight - ShellMotion.ContactSkin * 0.5f);
            Assert.NotEqual(MovementStepOutcome.EnvironmentUnresolved, jump.Outcome);
            var refused = scene.Binding.ValidatePlacement(scene.State(new(0, 1.7f, 0), false), queries);
            Assert.Equal(MovementStepOutcome.PlacementRefused, refused.Outcome);
        }
        scene.Registration.ApplyRuntimeStatics([RuntimeStaticChange.Remove(new("world", "ceiling")), RuntimeStaticRegistrationTests.Wall()]);
        using var next = scene.Acquire();
        var overlap = scene.State(new(2.7f, 0.75f, 0));
        var recovery = scene.Binding.StepTowards(overlap, -Vector2.UnitX, false, 0.1f, next);
        Assert.True(recovery.Outcome is MovementStepOutcome.Advanced or MovementStepOutcome.Blocked);
        Assert.True(recovery.State.State.Position.X < 2.55f);
        Assert.False(scene.Physics.ComputePenetration(new(scene.Tuning.CapsuleRadius,
            2 * (scene.Tuning.CapsuleHalfHeight - scene.Tuning.CapsuleRadius)), Pose.At(recovery.State.State.Position), out _));
    }

    [Fact]
    public void LeasedWaterMovementStillCollidesWithRuntimeSolids()
    {
        var tuning = OrdinaryContactScene.DefaultTuning with { Water = new(WaterTraversalMode.SurfaceSwimmer) };
        using var scene = new OrdinaryContactScene(tuning, (_, _) => -4,
            extent: new(new(-20, -4, -20), new(20, 4, 20), -4, -4), water: MovementWaterField.Level("world", 0, -4));
        using var baked = new OrdinaryContactScene(tuning, (_, _) => -4,
            extent: new(new(-20, -4, -20), new(20, 4, 20), -4, -4), water: MovementWaterField.Level("world", 0, -4),
            install: (physics, builder) =>
            {
                var handle = physics.AddStatic(new BoxShape(new(0.2f, 2, 3)), Pose.At(new(3, -1, 0)));
                builder.Add(handle, 0, int.MaxValue, new("world", "wet-wall"), new(2.8f, -3, -3), new(3.2f, 1, 3));
            });
        scene.Registration.ApplyRuntimeStatics([RuntimeStaticChange.Set(new("world", "wet-wall"),
            new BoxShape(new(0.2f, 2, 3)), Pose.At(new(3, -1, 0)))]);
        var state = scene.State(new(0, -0.15f, 0), false);
        var control = baked.State(new(0, -0.15f, 0), false);
        for (int tick = 0; tick < 60; tick++)
        {
            using var queries = scene.Acquire();
            var next = scene.Binding.StepTowards(state, Vector2.UnitX, false, 1f / 30, queries);
            using var reference = baked.Acquire();
            var expected = baked.Binding.StepTowards(control, Vector2.UnitX, false, 1f / 30, reference);
            RuntimeStaticScene.AssertBits(expected.State.State, next.State.State);
            Assert.Equal(expected.Outcome, next.Outcome);
            Assert.True(next.Outcome is MovementStepOutcome.Advanced or MovementStepOutcome.Blocked);
            state = next.State;
            control = expected.State;
        }
        Assert.True(state.State.Swimming);
        Assert.InRange(state.State.Position.X, 2.4f, 2.8f - tuning.CapsuleRadius - ShellMotion.ContactSkin * 0.5f);
    }

    static float StartPhase(int phase) => phase switch
    { 0 => 0, 1 => MathF.BitDecrement(0.125f), 2 => 0.125f, _ => MathF.BitIncrement(0.125f) };

    [Fact]
    public void RuntimeStairLookaheadMatchesAuthoredGroundSignals()
    {
        using var runtime = new RuntimeStaticScene(true);
        using var baked = new RuntimeStaticScene(true, install: (physics, builder) =>
        {
            for (int index = 0; index < 4; index++)
            {
                Vector3 min = new(0.4f + index * 0.25f, 0, -4), max = new(4, (index + 1) * 0.15f, 4);
                var handle = physics.AddStatic(new BoxShape((max - min) * 0.5f), Pose.At((max + min) * 0.5f));
                builder.Add(handle, 0, int.MaxValue, new("world", $"stair-{index}"), min, max);
            }
        });
        RuntimeStaticChange[] stairs = new RuntimeStaticChange[4];
        for (int index = 0; index < stairs.Length; index++)
        {
            Vector3 min = new(0.4f + index * 0.25f, 0, -4), max = new(4, (index + 1) * 0.15f, 4);
            stairs[index] = RuntimeStaticChange.Set(new("world", $"stair-{index}"), new BoxShape((max - min) * 0.5f),
                Pose.At((max + min) * 0.5f));
        }
        runtime.Scene.Registration.ApplyRuntimeStatics(stairs);
        var a = new MoveState { Position = new(0, 0.75f, 0), Grounded = true };
        var b = a;
        bool triggered = false;
        for (int tick = 0; tick < 20; tick++)
        {
            a = runtime.Tick(a, Vector2.UnitX).State;
            b = baked.Tick(b, Vector2.UnitX).State;
            RuntimeStaticScene.AssertBits(b, a);
            triggered |= a.ClimbRateEwma > 0;
        }
        Assert.True(triggered);
        Assert.True(a.Position.X > 1);
    }
}
