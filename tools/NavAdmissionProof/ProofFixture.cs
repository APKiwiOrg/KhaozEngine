using System;
using System.Numerics;
using System.Text;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;

namespace KhaozEngine.Tests.Movement.Research;

internal sealed class ProofFixture : IDisposable
{
    internal const string Identity = "public-flat-boxes-v1,164x204,0.25m,[-20.5,20.5)x[-25.5,25.5),floor-y0";
    internal const int Width = 164, Height = 204;
    internal static MoveTuning Tuning => MoveTuning.Default with
    {
        CapsuleRadius = 0.2f, CapsuleHalfHeight = 0.75f, MaxSlopeRadians = 0.8f,
        StepHeight = 0.4f, WalkSpeed = 9f, RunSpeed = 18f,
    };
    internal static PhysicsNavBakeOptions Options => new(-20.5f, -25.5f, 20.5f, 25.5f, 0.25f,
        5, 8, Tuning.MaxSlopeRadians, Width * Height, Width * Height * 4, MaxSurfacesPerColumn: 4);
    internal static NavBakeProfile Profile => new("legacy", Tuning, default);
    internal static NavBakeSources Sources => new NavBakeSources().AddHashOf("fixture", Encoding.UTF8.GetBytes(
        Identity + ";floor-half=(24,0.1,29),center=(0,-0.1,0)" +
        ";boxes-half/center=(0.6,1,0.6)/(0,1,0),(0.125,1,2)/(-6,1,5),(1.25,1,0.5)/(7,1,-8)"));

    private readonly BepuPhysicsWorld world;
    private readonly IDisposable view;
    private readonly GroundMoveContext context;
    internal PhysicsNavBake Capture { get; }

    internal ProofFixture()
    {
        world = new BepuPhysicsWorld();
        try
        {
            StaticHandle floor = world.AddStatic(new BoxShape(new(24, 0.1f, 29)), Pose.At(new(0, -0.1f, 0)));
            world.AddStatic(new BoxShape(new(0.6f, 1, 0.6f)), Pose.At(new(0, 1, 0)));
            world.AddStatic(new BoxShape(new(0.125f, 1, 2)), Pose.At(new(-6, 1, 5)));
            world.AddStatic(new BoxShape(new(1.25f, 1, 0.5f)), Pose.At(new(7, 1, -8)));
            var selectedView = world.CreateQueryViewExcludingStatics([floor]);
            view = selectedView;
            try
            {
                // Capture sees the complete owner. Movement sees matching analytic ground and props.
                context = new GroundMoveContext((_, _) => 0, (_, _) => Vector3.UnitY,
                    world, null, null, selectedView);
                try { Capture = PhysicsNavBake.Capture(context, Options, _ => 0); }
                catch { context.Dispose(); throw; }
            }
            catch { view.Dispose(); throw; }
        }
        catch { world.Dispose(); throw; }
    }

    internal static GraphCounts CheckGraph(GroundNavigation navigation)
    {
        NavTraversalGraph graph = CanonicalProfileFile.Graph(navigation);
        int accepted = 0, rejected = 0, edges = 0;
        foreach (NavTraversalLayer layer in graph.Layers)
        {
            if (layer.Width != Width || layer.Height != Height)
                throw new InvalidOperationException("The public fixture dimensions changed.");
            for (int z = 0; z < Height; z++)
                for (int x = 0; x < Width; x++)
                {
                    if (layer.IsAccepted(x, z)) accepted++;
                    else rejected++;
                    edges += BitOperations.PopCount((uint)layer.ExitMask(x, z));
                }
        }
        bool clearEdge = graph.CanTraverse(0, 10, 10, 0, 11, 10);
        bool obstacleRejected = !graph.IsNodePassable(0, 82, 102);
        if (accepted < 10000 || edges < 50000 || rejected == 0 || !clearEdge || !obstacleRejected)
            throw new InvalidOperationException("The public fixture lacks nontrivial open edges or obstacle refusals.");
        return new GraphCounts(graph.Layers.Count, accepted, rejected, edges,
            navigation.Space.Links.Count, graph.Links.Count, clearEdge, obstacleRejected);
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
