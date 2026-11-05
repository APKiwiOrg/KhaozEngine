using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Physics;

namespace KhaozEngine.Tests.Movement.Fixtures;

/// <summary>Continuous triangle ground, with a one-metre steep band between genuine upper and lower patches.</summary>
internal sealed class ContactTerrainFixture : IDisposable
{
    internal static MoveTuning Tuning => new(2, 5, 0.75f, MathF.PI / 4, CapsuleRadius: 0.3f,
        StepHeight: 0.4f, WadeMinSpeedScale: 0.6f, SwimSpeed: 3)
    { Controller = MovementControllerKind.Contact, Water = new(WaterTraversalMode.SurfaceSwimmer) };
    internal static PhysicsNavBakeOptions Options => new(-1, -0.75f, 2, 0.75f, 0.25f, 10, 14,
        Tuning.MaxSlopeRadians, 128, 512);
    internal OrdinaryContactScene Scene { get; }
    internal GroundMoveContext Context { get; }
    internal PhysicsNavBake Capture { get; }
    internal Vector3 From { get; }
    internal Vector3 To => new(1.375f, 0, 0.125f);

    internal ContactTerrainFixture(float height)
    {
        From = new(-0.375f, height, 0.125f);
        // Mirror certified tile ground. Capture reads the genuine mesh, while the registered movement selection
        // uses its exact height field and normal, as the installed terrain producer does.
        Scene = new(Tuning, (x, _) => x <= 0 ? height : x >= 1 ? 0 : height * (1 - x),
            (x, _) => x <= 0 || x >= 1 ? Vector3.UnitY : Vector3.Normalize(new(height, 1, 0)), extent: new(new(-20, -10, -20), new(20, 12, 20), -10, 10),
            install: (world, builder) =>
            {
                Vector3[] vertices = [new(-8, height, -8), new(0, height, -8), new(1, 0, -8), new(8, 0, -8),
                    new(-8, height, 8), new(0, height, 8), new(1, 0, 8), new(8, 0, 8)];
                int[] indices = [0, 1, 4, 1, 5, 4, 1, 2, 5, 2, 6, 5, 2, 3, 6, 3, 7, 6];
                var handle = world.AddStatic(new TriangleMeshShape(vertices, indices), Pose.Identity);
                builder.Add(handle, 0, 5, new("world", "terrain"), new(-8, 0, -8), new(8, height, 8));
                builder.Exclude(handle);
            });
        Context = ContactNavigationFixture.Context(Scene);
        Capture = PhysicsNavBake.Capture(Context, Options, _ => 0);
    }

    public void Dispose() { Capture.Dispose(); Context.Dispose(); Scene.Dispose(); }
}
