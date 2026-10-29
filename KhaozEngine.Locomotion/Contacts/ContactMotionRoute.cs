using System;
using System.Numerics;

namespace KhaozEngine.Locomotion.Contacts;

/// <summary>Bounded, attempt-local placement evidence, streamed at each real route vertex. A rejected attempt
/// cannot lend its proof to a retry. Substeps start fresh routes rather than truncating a whole tick's path.</summary>
internal struct ContactMotionRoute
{
    const int MaximumSegments = 6 * MovementCapsuleResolver.MaximumEndpoints + 4;
    readonly ContactQueryContext _queries;
    readonly float _startY;
    int _segments;
    internal ContactMotionRoute(ContactQueryContext queries, Vector3 start) =>
        (_queries, _startY, Current) = (queries, start.Y, start);
    internal Vector3 Current { get; private set; }
    internal bool Denied { get; private set; }
    internal float Highest { get; private set; }
    internal float Lowest { get; private set; }

    internal bool Append(Vector3 target)
    {
        if (Denied) return false;
        Count();
        if (!_queries.AcceptSegment(Current, target) || !_queries.AcceptRouteSegment(Current, target))
        { Denied = true; return false; }
        Record(target);
        return true;
    }

    internal bool Move(Vector3 target, out bool blocked, Span<Vector3> velocity = default)
    {
        blocked = false;
        if (Denied) return false;
        Span<Vector3> path = stackalloc Vector3[MovementCapsuleResolver.MaximumEndpoints];
        if (!_queries.ResolveSegment(Current, target, path, out int written, out blocked, velocity))
        { Denied = true; return false; }
        for (int i = 0; i < written; i++)
        {
            Count();
            if (!_queries.AcceptRouteSegment(Current, path[i])) { Denied = true; return false; }
            Record(path[i]);
        }
        return true;
    }

    void Record(Vector3 target)
    {
        Current = target;
        Highest = MathF.Max(Highest, target.Y - _startY);
        Lowest = MathF.Min(Lowest, target.Y - _startY);
    }

    void Count()
    {
        if (++_segments > MaximumSegments)
            throw new ContactQueryFailure(MovementAvailability.CapacityExceeded);
    }
}
