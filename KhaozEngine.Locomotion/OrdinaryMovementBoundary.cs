using System;
using System.Numerics;

namespace KhaozEngine.Locomotion;

/// <summary>Captured absolute playable boundary expressed in one physical frame.</summary>
internal sealed class OrdinaryMovementBoundary : MovementBoundary
{
    readonly MovementBoundary _absolute;
    readonly MovementFrameDescriptor _frame;
    internal OrdinaryMovementBoundary(MovementBoundary absoluteBoundary, in MovementFrameDescriptor frame)
    {
        ArgumentNullException.ThrowIfNull(absoluteBoundary);
        _absolute = absoluteBoundary;
        _frame = frame;
    }
    public override string SemanticIdentity => _absolute.SemanticIdentity;

    public override MovementAvailability Classify(Vector3 centre, out bool inside)
    {
        inside = false;
        if (!Exact(centre, out Vector3 absolute)) return MovementAvailability.Unresolved;
        return _absolute.Classify(absolute, out inside);
    }

    public override MovementAvailability Constrain(Vector3 start, Vector3 displacement, out Vector3 constrained)
    {
        constrained = default;
        if (!Exact(start, out Vector3 absolute)) return MovementAvailability.Unresolved;
        MovementAvailability result = _absolute.Constrain(absolute, displacement, out Vector3 proposal);
        if (result != MovementAvailability.Known) return result;
        Vector3 storedEnd = start + proposal;
        if (!Exact(storedEnd, out Vector3 end) ||
            (double)end.X - absolute.X != (double)storedEnd.X - start.X ||
            (double)end.Y - absolute.Y != (double)storedEnd.Y - start.Y ||
            (double)end.Z - absolute.Z != (double)storedEnd.Z - start.Z)
            return MovementAvailability.Unresolved;
        result = _absolute.ProveSegment(absolute, proposal, end, out bool admitted);
        if (result != MovementAvailability.Known) return result;
        if (!admitted) return MovementAvailability.Unresolved;
        constrained = proposal;
        return MovementAvailability.Known;
    }

    public override MovementAvailability ProveSegment(Vector3 start, Vector3 displacement, Vector3 storedEnd, out bool admitted)
    {
        admitted = false;
        if (!Exact(start, out Vector3 absolute) || !Exact(storedEnd, out Vector3 end) ||
            (double)end.X - absolute.X != (double)storedEnd.X - start.X ||
            (double)end.Y - absolute.Y != (double)storedEnd.Y - start.Y ||
            (double)end.Z - absolute.Z != (double)storedEnd.Z - start.Z)
            return MovementAvailability.Unresolved;
        return _absolute.ProveSegment(absolute, displacement, end, out admitted);
    }

    bool Exact(Vector3 local, out Vector3 absolute)
    {
        absolute = default;
        if (!MovementEnvironmentValidation.Finite(local)) return false;
        try { absolute = OrdinaryMovementEnvironment.Translate(local, _frame.PhysicsOrigin, out double error); return error == 0; }
        catch (ArgumentException) { return false; }
    }
}
