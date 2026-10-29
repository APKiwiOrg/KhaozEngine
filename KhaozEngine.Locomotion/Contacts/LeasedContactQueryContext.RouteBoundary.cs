using System.Numerics;

namespace KhaozEngine.Locomotion.Contacts;

internal sealed partial class LeasedContactQueryContext
{
    int _airRouteDepth;

    internal bool BeginAirRoute(out int prior)
    {
        prior = _airRouteDepth;
        if (_boundary is not IMovementRouteBoundary) return false;
        _airRouteDepth++;
        return true;
    }

    internal void EndAirRoute(int prior) => _airRouteDepth = prior;

    internal void RecordAcceptedSupport(Vector3 feet)
    {
        if (_boundary is IMovementRouteBoundary route) route.RecordSupportedCentre(BodyCentre(feet));
    }

    internal bool AcceptRouteSegment(Vector3 from, Vector3 to)
        => _airRouteDepth > 0 || AcceptResolvedRouteSegment(from, to);

    internal bool AcceptResolvedRouteSegment(Vector3 from, Vector3 to)
    {
        if (_boundary is not IMovementRouteBoundary route) return true;
        Vector3 start = BodyCentre(from), end = BodyCentre(to);
        Require(route.ProveRouteSegment(start, end - start, end, out bool admitted));
        return admitted;
    }
}
