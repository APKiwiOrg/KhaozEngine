using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Movement.Fixtures;

internal sealed class OrdinaryContactScene : IDisposable
{
    internal BepuPhysicsWorld Physics { get; } = new(Vector3.Zero);
    internal MoveTuning Tuning { get; }
    internal OrdinaryMovementRegistration Registration { get; }
    internal EffectiveMovementBinding Binding { get; }
    internal MovementQueryScope Scope { get; }
    internal MovementFrameDescriptor Frame { get; }
    internal Func<float, float, float> Height { get; }
    internal Func<float, float, Vector3>? Normal { get; }

    internal static MoveTuning DefaultTuning => new(4, 6, 0.75f, 20f * MathF.PI / 180f,
        CapsuleRadius: 0.25f, TractionHysteresisRadians: 0f)
    { Controller = MovementControllerKind.Contact, Water = new(WaterTraversalMode.DryOnly) };

    internal OrdinaryContactScene(MoveTuning? tuning = null, Func<float, float, float>? height = null,
        Func<float, float, Vector3>? normal = null, OrdinaryMovementExtent? extent = null,
        IMovementWaterField? water = null, Vector3? origin = null,
        Action<BepuPhysicsWorld, OrdinaryMovementRegistration.Builder>? install = null)
    {
        Tuning = tuning ?? DefaultTuning;
        Height = height ?? ((_, _) => 0f);
        Normal = normal;
        var builder = OrdinaryMovementRegistration.CreateBuilder("world", Physics,
            extent ?? new(new(-20, -4, -20), new(20, 4, 20), 0, 0));
        install?.Invoke(Physics, builder);
        if (origin.HasValue) Physics.Rebase(origin.Value);
        using (var lease = Physics.AcquireQueryReadLease()) Registration = builder.Build(lease);
        Binding = EffectiveMovementBinding.Ordinary(Tuning, Physics, Registration, Height, Normal,
            MovementBoundary.Rectangle(-20, -20, 20, 20), water);
        Frame = new(WorldFrame.Origin, Physics.Origin, 1);
        Scope = Binding.CreateOrdinaryScope(Frame);
    }

    internal MovementQueryLease Acquire()
    {
        Assert.Equal(MovementAvailability.Known, Binding.TryAcquire(Scope, out var lease));
        return Assert.IsType<MovementQueryLease>(lease);
    }

    internal FramedMovementState State(Vector3 centre, bool grounded = true) =>
        new(new MoveState { Position = centre, Grounded = grounded }, Frame, null);

    public void Dispose() { Binding.Dispose(); Physics.Dispose(); }
}
