using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using KhaozEngine.Navigation;

namespace KhaozEngine.Movement;

public sealed partial class GroundNavigation
{
    /// <summary>The closest graph cell under a footprint-admitted endpoint.</summary>
    internal readonly record struct Node(int Layer, int X, int Z);

    /// <summary>Tests a layer in increasing index order. Equal height distances keep the earlier layer.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ProbeEndpointLayer(Vector3 feet, int layer, ref double best, ref Node node)
    {
        NavGrid grid = Space.Layers[layer];
        (int x, int z) = grid.CellOf(feet.X, feet.Z);
        float? height = grid.RestingHeightAt(x, z);
        if (height is not float y) return;
        double distance = Math.Abs((double)feet.Y - y);
        if (distance >= best) return;
        best = distance;
        node = new Node(layer, x, z);
    }

    /// <summary>Allows enough vertical reach for an in-progress grounded step to an accepted node.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool AcceptsEndpoint(double distance, in Node node)
        => distance <= Math.Max(_tuning.Step, GroundTraversalProbe.ArrivalTolerance) &&
            Graph.IsNodePassable(node.Layer, node.X, node.Z);

    /// <summary>Identifies a finished axis or one that the current edge leaves unchanged.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool SameCell(int cell, int target) => cell == target;

    internal static double Crossing(float start, double delta, float origin, float size, int cell, int direction)
        => direction == 0 ? double.PositiveInfinity :
            (origin + (cell + (direction > 0 ? 1d : 0d)) * size - start) / delta;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static double CrossingStride(NavGrid grid, double delta, int direction)
        => direction == 0 ? double.PositiveInfinity : grid.CellSize / Math.Abs(delta);

    /// <summary>Equal boundary crossings take the diagonal edge directly.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void NextCrossing(int x, int z, int sx, int sz, double nextX, double nextZ, out int nx, out int nz)
    {
        nx = x;
        nz = z;
        if (nextX <= nextZ) nx += sx;
        if (nextZ <= nextX) nz += sz;
    }

    /// <summary>Advances a crossed axis and removes it from consideration as soon as it reaches its target.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static double AfterCrossing(int next, int target, double crossing, double stride)
        => next == target ? double.PositiveInfinity : crossing + stride;
}
