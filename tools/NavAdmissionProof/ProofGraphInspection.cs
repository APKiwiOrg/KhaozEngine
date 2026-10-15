using System.Numerics;
using KhaozEngine.Navigation;

namespace KhaozEngine.Tests.Movement.Research;

internal static class ProofGraphInspection
{
    internal static GraphAcceptance Inspect(NavTraversalGraph graph)
    {
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
        ObstacleProbe obstacle = ObstacleProbe.Inspect(graph);
        return new GraphAcceptance(new GraphCounts(graph.Layers.Count, accepted, rejected, edges,
            graph.Space.Links.Count, graph.Links.Count, clearEdge, obstacle.Passed), dimensions)
        { ObstacleProbe = obstacle };
    }
}
