using System;
using System.Runtime.CompilerServices;
using KhaozEngine.Navigation;

namespace KhaozEngine.Movement;

internal sealed partial class RouteStraightener
{
    /// <summary>Clears the visible straightened route and captures the one-shot fallback for this plan.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool BeginStraightening()
    {
        LastStraightened = null;
        if (!_fallBack) return false;
        _fallBack = false;
        return true;
    }

    /// <summary>Records the published route only if straightening removed a waypoint.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void RecordStraightened(NavPath? path) => LastStraightened = path;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool CanStraighten(NavPath path) => path.Status != NavPathStatus.Unreachable;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool HasShortcutCandidates(int waypointCount) => waypointCount >= 2;

    // The immediate raw edge is always available without a guard. Candidates start one past it.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int NextEdge(int anchor) => anchor + 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int FirstShortcut(int anchor) => anchor + 2;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsWalk(NavWaypoint waypoint) => waypoint.Kind == NavWaypointKind.Walk;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool OnLayer(NavWaypoint waypoint, int layer) => waypoint.Layer == layer;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool DroppedWaypoints(int kept, int original) => kept != original;

    /// <summary>After the centre guard passes, compute one offset vector for the positive then negative
    /// guards. A zero-length segment needs no side lines.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static double SideLength(double dx, double dz) => Math.Sqrt(dx * dx + dz * dz);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static double Delta(float to, float from) => (double)to - from;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool NoSideLines(double length) => length == 0d;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal double SideScale(int layer, double length) => SideOffsetCells * (double)_space.Layers[layer].CellSize / length;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static double SideOffsetX(double dz, double scale) => -dz * scale;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static double SideOffsetZ(double dx, double scale) => dx * scale;
}
