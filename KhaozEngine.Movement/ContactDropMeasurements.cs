using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Navigation;

namespace KhaozEngine.Movement;

internal readonly record struct ContactDropCandidateMeasurement(NavLink Link, double ExpectedFallMetres, bool Walk, bool Run);
internal readonly record struct ContactDropStepObservation(Vector3 Source, Vector3 Feet, bool Run, bool Hold,
    MovementStepOutcome Outcome, bool Grounded, Vector2 CommandedVelocity)
{
    internal bool UnwalkableSupport { get; init; }
}
