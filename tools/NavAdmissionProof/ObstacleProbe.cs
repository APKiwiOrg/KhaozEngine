using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using KhaozEngine.Navigation;

namespace KhaozEngine.Tests.Movement.Research;

internal sealed record GraphProbeNode(int Layer, int X, int Z,
    [property: JsonNumberHandling(JsonNumberHandling.AllowNamedFloatingPointLiterals)] float WorldX,
    [property: JsonNumberHandling(JsonNumberHandling.AllowNamedFloatingPointLiterals)] float WorldZ,
    bool InBounds,
    [property: JsonNumberHandling(JsonNumberHandling.AllowNamedFloatingPointLiterals)] float? SurfaceHeight,
    [property: JsonNumberHandling(JsonNumberHandling.AllowNamedFloatingPointLiterals)] float? RestingHeight, bool Passable)
{
    public bool InsideBox => MathF.Abs(WorldX) < 0.6f && MathF.Abs(WorldZ) < 0.6f;
}

internal sealed record GraphProbeTraversal(GraphProbeNode From, GraphProbeNode To, bool Accepted, string Kind);

internal sealed record ObstacleProbe(int Layers, GraphProbeNode[]? Nodes,
    GraphProbeTraversal[]? Neighbors, GraphProbeTraversal[]? Links)
{
    private const float CellSize = 0.25f, ProbeMin = -0.875f, Roof = 2f, HeightTolerance = 0.001f;
    private const int Side = 8, NodesPerLayer = Side * Side, NeighborsPerLayer = 604;

    public bool DataComplete
    {
        get
        {
            if (Layers <= 0 || Nodes is null || Neighbors is null || Links is null ||
                Nodes.LongLength != (long)Layers * NodesPerLayer ||
                Neighbors.LongLength != (long)Layers * NeighborsPerLayer ||
                !Nodes.All(ValidNode) || !Neighbors.All(ValidTraversal) || !Links.All(ValidTraversal)) return false;
            if (Nodes.Select(node => (node.Layer, node.X, node.Z)).Distinct().Count() != Nodes.Length ||
                Nodes.Select(node => (node.Layer, node.WorldX, node.WorldZ)).Distinct().Count() != Nodes.Length)
                return false;
            foreach (var layer in Nodes.GroupBy(node => node.Layer))
            {
                if (layer.Count() != NodesPerLayer ||
                    layer.Any(node => !OnProbeAxis(node.WorldX) || !OnProbeAxis(node.WorldZ)) ||
                    layer.Select(node => node.WorldX - node.X * CellSize).Distinct().Count() != 1 ||
                    layer.Select(node => node.WorldZ - node.Z * CellSize).Distinct().Count() != 1) return false;
            }
            var nodes = Nodes.ToHashSet();
            if (Neighbors.Any(edge => edge.Kind != "Neighbor" ||
                !nodes.Contains(edge.From) && !nodes.Contains(edge.To) || edge.From.Layer != edge.To.Layer ||
                Math.Abs(edge.From.X - edge.To.X) > 1 || Math.Abs(edge.From.Z - edge.To.Z) > 1 ||
                edge.From == edge.To || edge.To.WorldX - edge.From.WorldX != (edge.To.X - edge.From.X) * CellSize ||
                edge.To.WorldZ - edge.From.WorldZ != (edge.To.Z - edge.From.Z) * CellSize) ||
                Neighbors.DistinctBy(edge => (edge.From.Layer, edge.From.X, edge.From.Z, edge.To.X, edge.To.Z))
                    .Count() != Neighbors.Length)
                return false;
            if (Nodes.Concat(Neighbors.SelectMany(edge => new[] { edge.From, edge.To }))
                .GroupBy(node => (node.Layer, node.X, node.Z)).Any(group => group.Distinct().Count() != 1)) return false;
            return Links.All(edge => Enum.TryParse(edge.Kind, out NavLinkKind kind) && Enum.IsDefined(kind));
        }
    }

    public bool GroundNodesRejected => Nodes is not null && Nodes.All(node => node is not null &&
        (!node.InsideBox || !node.Passable || AtRoof(node.SurfaceHeight) && AtRoof(node.RestingHeight)));
    public bool IllegalTraversalRejected => Neighbors is not null && Links is not null &&
        Neighbors.Concat(Links).All(edge => edge is not null && (!edge.Accepted || !Illegal(edge)));
    public bool ClearGroundBesideBox => ClearPair(-1.125f, 0.125f, -0.875f, 0.125f) &&
        ClearPair(0.875f, 0.125f, 1.125f, 0.125f) && ClearPair(0.125f, -1.125f, 0.125f, -0.875f) &&
        ClearPair(0.125f, 0.875f, 0.125f, 1.125f);
    public bool Passed => DataComplete && GroundNodesRejected && IllegalTraversalRejected && ClearGroundBesideBox;

    internal static ObstacleProbe Inspect(NavTraversalGraph graph)
    {
        var nodes = new List<GraphProbeNode>();
        var neighbors = new Dictionary<(int Layer, int X, int Z, int ToX, int ToZ), GraphProbeTraversal>();
        for (int layer = 0; layer < graph.Layers.Count; layer++)
        {
            NavGrid grid = graph.Space.Layers[layer];
            (int minX, int minZ) = grid.CellOf(ProbeMin, ProbeMin);
            for (int z = minZ; z < minZ + Side; z++)
                for (int x = minX; x < minX + Side; x++)
                {
                    GraphProbeNode from = ReadNode(graph, layer, x, z);
                    nodes.Add(from);
                    for (int dz = -1; dz <= 1; dz++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx, nz = z + dz;
                            if (dx == 0 && dz == 0) continue;
                            GraphProbeNode to = ReadNode(graph, layer, nx, nz);
                            neighbors.TryAdd((layer, x, z, nx, nz), new(from, to,
                                graph.CanTraverse(layer, x, z, layer, nx, nz), "Neighbor"));
                            neighbors.TryAdd((layer, nx, nz, x, z), new(to, from,
                                graph.CanTraverse(layer, nx, nz, layer, x, z), "Neighbor"));
                        }
                }
        }
        GraphProbeTraversal[] links = graph.Space.Links.Select(link => new GraphProbeTraversal(
            ReadNode(graph, link.FromLayer, link.FromX, link.FromZ),
            ReadNode(graph, link.ToLayer, link.ToX, link.ToZ), graph.Links.Contains(link), link.Kind.ToString())).ToArray();
        return new(graph.Layers.Count, nodes.ToArray(), neighbors.Values.ToArray(), links);
    }

    private bool ValidNode(GraphProbeNode? node) => node is not null && node.Layer >= 0 && node.Layer < Layers &&
        node.X >= 0 && node.Z >= 0 && node.InBounds && float.IsFinite(node.WorldX) && float.IsFinite(node.WorldZ) &&
        FiniteOrNull(node.SurfaceHeight) && FiniteOrNull(node.RestingHeight) &&
        (!node.Passable || node.SurfaceHeight.HasValue && node.RestingHeight.HasValue);

    private bool ValidTraversal(GraphProbeTraversal? edge) => edge is not null &&
        ValidNode(edge.From) && ValidNode(edge.To) && (!edge.Accepted || edge.From.Passable && edge.To.Passable);

    private static bool OnProbeAxis(float value)
    {
        float cell = (value - ProbeMin) / CellSize;
        return cell >= 0 && cell < Side && cell == MathF.Floor(cell);
    }

    private static bool FiniteOrNull(float? value) => value is null || float.IsFinite(value.Value);
    private static bool AtRoof(float? value) => AtHeight(value, Roof);
    private static bool AtHeight(float? value, float expected) =>
        value is float height && MathF.Abs(height - expected) <= HeightTolerance;

    private bool ClearPair(float x, float z, float toX, float toZ) =>
        ClearEdge(x, z, toX, toZ) && ClearEdge(toX, toZ, x, z);

    private bool ClearEdge(float x, float z, float toX, float toZ) => Neighbors is not null &&
        Neighbors.Any(edge => edge is { Accepted: true, From: not null, To: not null } &&
            edge.From.WorldX == x && edge.From.WorldZ == z && edge.To.WorldX == toX && edge.To.WorldZ == toZ &&
            AtHeight(edge.From.SurfaceHeight, 0) && AtHeight(edge.From.RestingHeight, 0) &&
            AtHeight(edge.To.SurfaceHeight, 0) && AtHeight(edge.To.RestingHeight, 0));

    private static bool Illegal(GraphProbeTraversal edge)
    {
        if (edge.From is null || edge.To is null || edge.From.RestingHeight is not float from ||
            edge.To.RestingHeight is not float to) return true;
        // An accepted descent is directional evidence. It does not authorize the reverse ascent.
        if (edge.To.InsideBox && to - from > ProofFixture.Tuning.StepHeight) return true;
        return from < Roof - HeightTolerance && to < Roof - HeightTolerance && CrossesBox(edge.From, edge.To);
    }

    private static bool CrossesBox(GraphProbeNode from, GraphProbeNode to)
    {
        double min = 0, max = 1;
        return Clip(from.WorldX, to.WorldX, ref min, ref max) && Clip(from.WorldZ, to.WorldZ, ref min, ref max);
    }

    private static bool Clip(float from, float to, ref double min, ref double max)
    {
        double delta = (double)to - from;
        if (delta == 0) return Math.Abs(from) < 0.6;
        double a = (-0.6 - from) / delta, b = (0.6 - from) / delta;
        min = Math.Max(min, Math.Min(a, b));
        max = Math.Min(max, Math.Max(a, b));
        return min < max;
    }

    private static GraphProbeNode ReadNode(NavTraversalGraph graph, int layer, int x, int z)
    {
        NavGrid grid = graph.Space.Layers[layer];
        var center = grid.CellCenter(x, z);
        return new(layer, x, z, center.X, center.Y, grid.InBounds(x, z),
            grid.SurfaceHeightAt(x, z), grid.RestingHeightAt(x, z), graph.IsNodePassable(layer, x, z));
    }
}
