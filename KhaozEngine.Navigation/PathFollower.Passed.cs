using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Navigation;

public sealed partial class PathFollower
{
    // Float steps of slack beside AcceptRadius on the lateral test. At 128 to 192 m one float step is about
    // 1.5e-5 m, above a strict 1e-5 m radius, so a body on the line would otherwise miss by rounding alone.
    const double PassedLateralFloatSteps = 4d;

    /// <summary>
    /// Whether the feet have passed the active waypoint on a straight run. The active waypoint must be a
    /// <see cref="NavPath.IsCollinearPassThrough"/> waypoint on the agent's layer (when the follower has a
    /// space), the feet must sit at or beyond it along the line to its successor, and their lateral distance
    /// from that line must be at most <see cref="PathFollowConfig.AcceptRadius"/> plus four float steps at the
    /// largest absolute coordinate involved. All geometry runs in double precision.
    /// </summary>
    bool PassedActive(Vector2 feetXz, int? agentLayer)
    {
        if (!_path!.IsCollinearPassThrough(_index))
            return false;

        IReadOnlyList<NavWaypoint> waypoints = _path.Waypoints;
        NavWaypoint active = waypoints[_index];
        if (agentLayer is not null && active.Layer != agentLayer.Value)
            return false;

        Vector2 w = active.Position;
        Vector2 n = waypoints[_index + 1].Position;
        double dx = (double)n.X - w.X;
        double dz = (double)n.Y - w.Y;
        double px = (double)feetXz.X - w.X;
        double pz = (double)feetXz.Y - w.Y;
        if (px * dx + pz * dz < 0d)
            return false;

        double lateral = Math.Abs(px * dz - pz * dx) / Math.Sqrt(dx * dx + dz * dz);
        float m = MathF.Max(MaxAbs(w), MathF.Max(MaxAbs(n), MaxAbs(feetXz)));
        double u = (double)MathF.BitIncrement(m) - m;
        return lateral <= _config.AcceptRadius + PassedLateralFloatSteps * u;
    }

    static float MaxAbs(Vector2 v) => MathF.Max(MathF.Abs(v.X), MathF.Abs(v.Y));
}
