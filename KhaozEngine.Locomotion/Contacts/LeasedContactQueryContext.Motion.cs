using System;
using System.Numerics;

namespace KhaozEngine.Locomotion.Contacts;

internal sealed partial class LeasedContactQueryContext
{
    WaterTraversalMode _waterMode;
    MovementBoundary? _boundary;
    internal MovementStepOutcome Outcome { get; private set; } = MovementStepOutcome.Advanced;
    internal bool RecoveryDenied { get; private set; }

    internal void Refuse(ContactQueryFailure failure) => Outcome = failure.Outcome;
    internal void RefuseRecovery() => (RecoveryDenied, Outcome) = (true, MovementStepOutcome.Blocked);
    internal void AcceptRoute(bool blocked)
    {
        if (blocked) Outcome = MovementStepOutcome.Blocked;
    }

    internal void ValidatePlacement(Vector3 feet)
    {
        if (!AcceptSegment(feet, feet))
            throw new ContactQueryFailure(MovementAvailability.Known, MovementStepOutcome.PlacementRefused);
    }

    internal bool AcceptSegment(Vector3 from, Vector3 to)
    {
        MovementBodyQuery body = Body(from);
        Vector3 end = Body(to).Centre;
        Vector3 delta = end - body.Centre;
        Require(_lease.CheckContactBodyPath(body, delta));
        Span<Vector3> normals = stackalloc Vector3[MovementQueryLease.MaxDomainContacts];
        Require(MovementSegmentProof.Medium(body, delta, _lease, _waterMode, _tuning.SwimEnterDepthFraction,
            _boundary, normals, out _, out var hit, end));
        return !hit.Blocked;
    }

    internal bool ResolveSegment(Vector3 from, Vector3 to, Span<Vector3> path, out int written, out bool blocked,
        Span<Vector3> velocity = default)
    {
        (written, blocked) = (0, false);
        MovementBodyQuery body = Body(from);
        Vector3 delta = Body(to).Centre - body.Centre;
        Require(_lease.CheckContactBodyPath(body, delta));
        if (AcceptSegment(from, to))
        {
            path[0] = to;
            written = 1;
            return true;
        }
        var shape = ShellGeometry.Shape(_tuning, Shapes);
        MovementBodyQuery shell = ShellBody(shape, ShellGeometry.Centre(from, _tuning));
        Require(MovementCapsuleResolver.TryResolveContact(shell, body, delta, _lease, _waterMode,
            _tuning.SwimEnterDepthFraction, _boundary, path, out written, out blocked, velocity,
            horizontal: velocity.IsEmpty && from.Y == to.Y));
        // The resolver proves physical centres. The shared passes report kernel feet, whose mapping and
        // endpoint rounding must earn their own proof before they can become a selected route vertex.
        Vector3 previous = from;
        for (int i = 0; i < written; i++)
        {
            Vector3 feet = from + (path[i] - body.Centre);
            if (!AcceptSegment(previous, feet)) throw new ContactQueryFailure(MovementAvailability.Unresolved);
            path[i] = feet;
            previous = feet;
        }
        return true;
    }
}
