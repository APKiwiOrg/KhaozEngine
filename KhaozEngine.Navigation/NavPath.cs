using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Navigation;

/// <summary>
/// Outcome of an <see cref="IPathPlanner.FindPath"/> query.
/// </summary>
public enum NavPathStatus
{
    /// <summary>A route to the goal was found. <see cref="NavPath.Waypoints"/> reaches it.</summary>
    Complete,

    /// <summary>The search spent its whole <see cref="PathQueryBudget"/> before reaching the goal.
    /// <see cref="NavPath.Waypoints"/> only reaches as far as the search got.</summary>
    Partial,

    /// <summary>No route exists, or the query could not be resolved (for example an endpoint failed
    /// to snap onto a passable cell). <see cref="NavPath.Waypoints"/> is empty.</summary>
    Unreachable,
}

/// <summary>
/// One point on a <see cref="NavPath"/>: a world XZ position and the <see cref="NavSpace"/> layer it
/// belongs to. Y is not stored here, callers resolve height from the layer's own grid or terrain data.
/// </summary>
/// <param name="Position">World XZ position of this waypoint.</param>
/// <param name="Layer">Index into <see cref="NavSpace.Layers"/> this waypoint lives on.</param>
public readonly record struct NavWaypoint(Vector2 Position, int Layer);

/// <summary>
/// Result of a path query: a <see cref="NavPathStatus"/> plus the waypoints leading toward the goal, in
/// travel order. Immutable.
/// </summary>
public sealed class NavPath
{
    static readonly NavPath _unreachable = new(NavPathStatus.Unreachable, Array.Empty<NavWaypoint>());

    /// <summary>How far the query got.</summary>
    public NavPathStatus Status { get; }

    /// <summary>The waypoints toward the goal, in travel order. Empty when <see cref="Status"/> is
    /// <see cref="NavPathStatus.Unreachable"/>.</summary>
    public IReadOnlyList<NavWaypoint> Waypoints { get; }

    /// <summary>Builds a path result from a <paramref name="status"/> and its
    /// <paramref name="waypoints"/>.</summary>
    public NavPath(NavPathStatus status, IReadOnlyList<NavWaypoint> waypoints)
    {
        Status = status;
        Waypoints = waypoints ?? throw new ArgumentNullException(nameof(waypoints));
        if (status == NavPathStatus.Unreachable && Waypoints.Count != 0)
        {
            throw new ArgumentException(
                "An Unreachable NavPath must carry zero waypoints.", nameof(waypoints));
        }
    }

    /// <summary>Shared, cached result for an unreachable query: <see cref="NavPathStatus.Unreachable"/>
    /// with no waypoints. Reuse this instead of constructing a new empty <see cref="NavPath"/>.</summary>
    public static NavPath Unreachable => _unreachable;
}
