using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion.Contacts;

internal static partial class GroundCore
{
    partial struct Tick
    {
        readonly Attempt DeniedMediumSeat(Attempt lifted, Attempt standing)
        {
            if (!_queries.HasMediumProof) return default;
            if (lifted.Moved && lifted.Seat.Outcome == SeatOutcome.Seated) return lifted;
            return standing.Moved && standing.Seat.Outcome == SeatOutcome.Seated ? standing : default;
        }

        // A forbidden down pass is an obstruction to the seated proposal. Retry its feasible horizontal
        // component through the same shell, seat and placement passes, under the same budget and pin.
        readonly Attempt Resolve(Vector2 move, Vector2? planned, double budget)
        {
            Attempt attempt = ResolveUnconstrained(move, planned, budget);
            if (!_queries.HasMediumProof) return attempt;
            SupportQueryDiagnostics.Route(SupportQueryRoute.MediumResolve);
            bool constrained = false;
            for (int retry = 0; retry < MovementCapsuleResolver.MaximumCorrections; retry++)
            {
                if (attempt.Placement.Valid || !attempt.Moved || attempt.Seat.Outcome != SeatOutcome.Seated)
                    break;
                Vector2 axis = _startAxis + attempt.Total;
                var from = new Vector3(Axis.X, FeetY, Axis.Y);
                var to = new Vector3(axis.X, attempt.Seat.FeetY, axis.Y);
                if (!_queries.ConstrainGroundDescent(from, to, attempt.Seat.Support.Normal,
                        _footRadius, _cosMaxSlope, out Vector2 feasible))
                    break;
                SupportQueryDiagnostics.Record(SupportQueryCounter.MediumRetries);
                SupportQueryDiagnostics.MediumRetryMaximum(retry + 1);
                constrained = true;
                attempt = ResolveUnconstrained(feasible, null, budget);
            }
            return constrained ? attempt with { Blocked = true } : attempt;
        }
    }
}
