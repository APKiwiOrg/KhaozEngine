using System;

namespace KhaozEngine.Navigation;

public sealed partial class NavPath
{
    // Sine of the largest bend still read as straight. Cell routes turn by 45 or 90 degrees, so this never
    // admits a grid corner.
    const double CollinearSineTolerance = 1e-4;

    /// <summary>
    /// Whether the waypoint at <paramref name="index"/> lies on a straight run, so a body may pass it without a
    /// turn. True when the waypoint has a predecessor and a successor, it and its successor are
    /// <see cref="NavWaypointKind.Walk"/>, all three share one layer, and in double precision the incoming and
    /// outgoing XZ directions are nonzero, point the same way, and bend by a sine of at most 1e-4. The first and
    /// last waypoints are never pass-through. A corner, a reversal, a layer change or a hop is never
    /// pass-through, so a follower that consumes passed waypoints still stops on every mandatory turn.
    /// </summary>
    /// <param name="index">Index into <see cref="Waypoints"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside
    /// <see cref="Waypoints"/>.</exception>
    public bool IsCollinearPassThrough(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Waypoints.Count);
        if (index == 0 || index == Waypoints.Count - 1)
            return false;

        NavWaypoint previous = Waypoints[index - 1];
        NavWaypoint current = Waypoints[index];
        NavWaypoint next = Waypoints[index + 1];
        if (current.Kind != NavWaypointKind.Walk || next.Kind != NavWaypointKind.Walk)
            return false;
        if (previous.Layer != current.Layer || next.Layer != current.Layer)
            return false;

        double ax = (double)current.Position.X - previous.Position.X;
        double az = (double)current.Position.Y - previous.Position.Y;
        double bx = (double)next.Position.X - current.Position.X;
        double bz = (double)next.Position.Y - current.Position.Y;
        double aLength = Math.Sqrt(ax * ax + az * az);
        double bLength = Math.Sqrt(bx * bx + bz * bz);
        if (aLength == 0d || bLength == 0d)
            return false;

        double dot = ax * bx + az * bz;
        double cross = ax * bz - az * bx;
        return dot > 0d && Math.Abs(cross) <= CollinearSineTolerance * aLength * bLength;
    }
}
