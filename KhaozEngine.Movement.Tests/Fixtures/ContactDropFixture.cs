using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Physics.Bepu;

namespace KhaozEngine.Tests.Movement.Fixtures;

internal sealed class ContactDropFixture : IDisposable
{
    internal static MoveTuning Tuning => ContactNavigationFixture.Tuning with { WalkSpeed = 3, RunSpeed = 5 };
    internal static PhysicsNavBakeOptions Options => new(-1.5f, -0.75f, 1.5f, 0.75f,
        0.5f, 10, 14, Tuning.MaxSlopeRadians, 128, 512);

    internal OrdinaryContactScene Scene { get; }
    internal GroundMoveContext Context { get; }
    internal PhysicsNavBake Capture { get; }

    internal ContactDropFixture(float height = 4, MoveTuning? tuning = null,
        PhysicsNavBakeOptions? options = null, IMovementWaterField? water = null,
        Action<BepuPhysicsWorld, OrdinaryMovementRegistration.Builder>? install = null,
        MovementBoundary? boundary = null)
    {
        MoveTuning body = tuning ?? Tuning;
        Scene = new(body, (x, _) => x < 0 ? height : 0,
            extent: new(new(-20, -10, -20), new(20, 12, 20), 0, 10), water: water,
            install: (world, builder) =>
            {
                ContactNavigationFixture.Box(world, builder, "floor", new(-8, -1, -8), new(8, 0, 8));
                ContactNavigationFixture.Box(world, builder, "ledge", new(-8, 0, -8), new(0, height, 8));
                install?.Invoke(world, builder);
            }, boundary: boundary);
        Context = new(Scene.Height, Scene.Normal, Scene.Physics, null, null, null, water, Scene.Registration,
            boundary ?? MovementBoundary.Rectangle(-20, -20, 20, 20));
        Capture = PhysicsNavBake.Capture(Context, options ?? Options, _ => 0);
    }

    public void Dispose() { Capture.Dispose(); Context.Dispose(); Scene.Dispose(); }
}
