using System;
using System.Numerics;
using System.Text;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;

namespace KhaozEngine.Tests.Movement.Research;

internal sealed class ProofFixture : IDisposable
{
    internal const string Identity = "public-flat-boxes-v1,164x204,0.25m,[-20.5,20.5)x[-25.5,25.5),floor-y0";
    internal const int Width = 164, Height = 204;
    internal static MoveTuning Tuning => MoveTuning.Default with
    {
        CapsuleRadius = 0.2f,
        CapsuleHalfHeight = 0.75f,
        MaxSlopeRadians = 0.8f,
        StepHeight = 0.4f,
        WalkSpeed = 9f,
        RunSpeed = 18f,
    };
    internal static PhysicsNavBakeOptions Options => new(-20.5f, -25.5f, 20.5f, 25.5f, 0.25f,
        5, 8, Tuning.MaxSlopeRadians, Width * Height, Width * Height * 4, MaxSurfacesPerColumn: 4);
    internal static NavBakeProfile Profile => new("legacy", Tuning, default);
    internal static NavBakeSources Sources => new NavBakeSources().AddHashOf("fixture", Encoding.UTF8.GetBytes(
        Identity + ";floor-half=(24,0.1,29),center=(0,-0.1,0)" +
        ";boxes-half/center=(0.6,1,0.6)/(0,1,0),(0.125,1,2)/(-6,1,5),(1.25,1,0.5)/(7,1,-8)"));

    private readonly PublicBoxCapture fixture = new(Options, distantObstacles: true);
    internal PhysicsNavBake Capture => fixture.Capture;

    internal static GraphAcceptance InspectGraph(GroundNavigation navigation, PhysicsNavBake capture)
    {
        NavTraversalGraph graph = CanonicalProfileFile.Graph(navigation);
        int accepted = 0, rejected = 0, edges = 0;
        var dimensions = new GraphLayerDimensions[graph.Layers.Count];
        int index = 0;
        foreach (NavTraversalLayer layer in graph.Layers)
        {
            dimensions[index] = new(index, layer.Width, layer.Height);
            index++;
            for (int z = 0; z < layer.Height; z++)
                for (int x = 0; x < layer.Width; x++)
                {
                    if (layer.IsAccepted(x, z)) accepted++;
                    else rejected++;
                    edges += BitOperations.PopCount((uint)layer.ExitMask(x, z));
                }
        }
        bool clearEdge = graph.CanTraverse(0, 10, 10, 0, 11, 10);
        GroundObstacleEvidence obstacle = GroundObstacleInspection.Inspect(navigation, capture);
        return new GraphAcceptance(new GraphCounts(graph.Layers.Count, accepted, rejected, edges,
            navigation.Space.Links.Count, graph.Links.Count, clearEdge, obstacle.ClearGroundApproaches,
            obstacle.AdjacentGroundObstructions, obstacle.GroundEntriesRefused), dimensions, obstacle);
    }

    internal void CheckScopeReleased() => fixture.CheckScopeReleased();
    public void Dispose() => fixture.Dispose();
}
