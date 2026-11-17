using System.Numerics;
using KhaozEngine.Replication;

namespace KhaozEngine.NetWorld;

internal static class RemotePresentationVelocity
{
    public static Vector2? Read(ClientReplicationView view, long netId, float tickSeconds)
    {
        if (!view.TryGetInterpolationSegment(netId, MoveProtocol.PositionTypeId,
                out ReplicatedPosition from, out ReplicatedPosition to, out long? fromTick, out long? toTick)
            || fromTick is not long first || toTick is not long last || last <= first)
            return null;

        double seconds = (last - first) * (double)tickSeconds;
        if (seconds <= 0 || !double.IsFinite(seconds)) return null;

        // Use the same frame as the position lerp to retain the precision of the two local endpoints.
        Vector3 displacement = to.Local - from.ToFrame(to.Frame).Local;
        return new Vector2((float)(displacement.X / seconds), (float)(displacement.Z / seconds));
    }
}
