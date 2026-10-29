using System;
using System.Numerics;

namespace KhaozEngine.Locomotion.Contacts;

/// <summary>Certifies resolved air substeps after foot support clips the shell's proposal. The knee-height
/// shell deliberately leaves the feet to support, so its unseated proposal is not an accepted body trajectory.
/// Horizontal and original-boundary proofs still run on every swept route segment.</summary>
internal readonly struct ContactAirRoute : IDisposable
{
    readonly ContactQueryContext _queries;
    readonly bool _active;
    readonly int _prior;

    internal ContactAirRoute(ContactQueryContext queries)
    {
        _queries = queries;
        _active = queries.BeginAirRoute(out _prior);
    }

    internal void Accept(Vector3 from, Vector3 to)
    {
        if (_active && !_queries.AcceptResolvedRouteSegment(from, to))
            throw new ContactQueryFailure(MovementAvailability.Known, MovementStepOutcome.Blocked);
    }

    public void Dispose()
    {
        if (_active) _queries.EndAirRoute(_prior);
    }
}
