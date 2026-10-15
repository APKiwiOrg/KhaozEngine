using System;
using System.Numerics;
using KhaozEngine.Movement;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;

namespace KhaozEngine.Tests.Movement.Research;

internal sealed class PublicBoxCapture : IDisposable
{
    internal static readonly Vector3 CentralHalfExtents = new(0.6f, 1, 0.6f);
    internal static readonly Vector3 CentralCenter = new(0, 1, 0);
    private readonly BepuPhysicsWorld world;
    private readonly IDisposable view;
    private readonly GroundMoveContext context;
    internal PhysicsNavBake Capture { get; }

    internal PublicBoxCapture(PhysicsNavBakeOptions options, bool centralObstacle = true, bool distantObstacles = false)
    {
        world = new BepuPhysicsWorld();
        try
        {
            StaticHandle floor = world.AddStatic(new BoxShape(new(24, 0.1f, 29)), Pose.At(new(0, -0.1f, 0)));
            if (centralObstacle) world.AddStatic(new BoxShape(CentralHalfExtents), Pose.At(CentralCenter));
            if (distantObstacles)
            {
                world.AddStatic(new BoxShape(new(0.125f, 1, 2)), Pose.At(new(-6, 1, 5)));
                world.AddStatic(new BoxShape(new(1.25f, 1, 0.5f)), Pose.At(new(7, 1, -8)));
            }
            var selectedView = world.CreateQueryViewExcludingStatics([floor]);
            view = selectedView;
            try
            {
                // Capture sees the complete owner. Movement sees matching analytic ground and props.
                context = new GroundMoveContext((_, _) => 0, (_, _) => Vector3.UnitY,
                    world, null, null, selectedView);
                try { Capture = PhysicsNavBake.Capture(context, options, _ => 0); }
                catch { context.Dispose(); throw; }
            }
            catch { view.Dispose(); throw; }
        }
        catch { world.Dispose(); throw; }
    }

    internal void CheckScopeReleased() => world.Rebase(Vector3.Zero);

    public void Dispose()
    {
        Capture.Dispose();
        context.Dispose();
        view.Dispose();
        world.Dispose();
    }
}
