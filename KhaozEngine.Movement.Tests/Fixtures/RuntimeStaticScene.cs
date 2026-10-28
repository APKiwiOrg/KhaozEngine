using System;
using System.Numerics;
using System.Reflection;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Physics;
using Xunit;

namespace KhaozEngine.Tests.Movement.Fixtures;

/// <summary>One owner and one mutable movement context per test instance, including Legacy selection.</summary>
internal sealed class RuntimeStaticScene : IDisposable
{
    internal OrdinaryContactScene Scene { get; }
    internal GroundMoveContext Context { get; }
    internal MoveTuning Tuning { get; }
    internal const float Dt = 1f / 30;

    internal RuntimeStaticScene(bool contact, Vector3 origin = default,
        Action<KhaozEngine.Physics.Bepu.BepuPhysicsWorld, OrdinaryMovementRegistration.Builder>? install = null)
    {
        Scene = new(origin: origin, extent: new(origin + new Vector3(-20, -8, -20), origin + new Vector3(20, 8, 20), origin.Y, origin.Y),
            height: (_, _) => origin.Y,
            boundary: MovementBoundary.Rectangle(origin.X - 20, origin.Z - 20, origin.X + 20, origin.Z + 20),
            install: (physics, builder) =>
            {
                var floor = physics.AddStatic(new BoxShape(new(20, 0.125f, 20)), Pose.At(origin + new Vector3(0, -0.125f, 0)));
                builder.Add(floor, 0, int.MaxValue, new("world", "baked-ground"), origin + new Vector3(-20, -0.25f, -20),
                    origin + new Vector3(20, 0, 20)).Exclude(floor);
                var pillar = physics.AddStatic(new BoxShape(Vector3.One), Pose.At(origin + new Vector3(-10, 1, -10)));
                builder.Add(pillar, 0, int.MaxValue, new("world", "untouched"), origin + new Vector3(-11, 0, -11),
                    origin + new Vector3(-9, 2, -9));
                install?.Invoke(physics, builder);
            });
        Tuning = contact ? Scene.Tuning : Scene.Tuning with { Controller = MovementControllerKind.Legacy, Water = default };
        Context = new(Scene.Height, null, Scene.Physics, null, null, null, null, Scene.Registration,
            MovementBoundary.Rectangle(origin.X - 20, origin.Z - 20, origin.X + 20, origin.Z + 20));
        Context.ValidateTuning(Tuning);
    }

    internal (MoveState State, MovementStepOutcome Outcome, MovementSupportKey? Support) Tick(MoveState body, Vector2 direction)
    {
        MoveState moved = Context.Step(body, direction, false, Dt, Tuning);
        if (Tuning.Controller == MovementControllerKind.Legacy) return (moved, MovementStepOutcome.Advanced, null);
        using var queries = Scene.Acquire();
        var result = Scene.Binding.StepTowards(new(body with { Position = body.Position - Scene.Physics.Origin }, Scene.Frame, null),
            direction, false, Dt, queries);
        MoveState absolute = result.State.State with { Position = result.State.State.Position + Scene.Physics.Origin };
        AssertBits(absolute, moved);
        Assert.True(result.Outcome is MovementStepOutcome.Advanced or MovementStepOutcome.Blocked, result.Outcome.ToString());
        return (moved, result.Outcome, result.State.Selection?.Support);
    }

    internal static void AssertBits(object? expected, object? actual)
    {
        if (expected is null || actual is null) { Assert.Equal(expected, actual); return; }
        Assert.Equal(expected.GetType(), actual.GetType());
        if (expected is float number)
        {
            Assert.Equal(BitConverter.SingleToInt32Bits(number), BitConverter.SingleToInt32Bits((float)actual));
            return;
        }
        if (expected.GetType().IsPrimitive || expected.GetType().IsEnum || expected is string)
        { Assert.Equal(expected, actual); return; }
        FieldInfo[] fields = expected.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (fields.Length == 0) { Assert.Equal(expected, actual); return; }
        foreach (FieldInfo field in fields) AssertBits(field.GetValue(expected)!, field.GetValue(actual)!);
    }

    public void Dispose() { Context.Dispose(); Scene.Dispose(); }
}
