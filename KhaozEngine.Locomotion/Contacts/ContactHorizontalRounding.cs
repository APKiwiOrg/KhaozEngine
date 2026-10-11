using System.Numerics;

namespace KhaozEngine.Locomotion.Contacts;

/// <summary>Retains the unrounded horizontal integral across accepted mode segments. A contact or recovery
/// rebases it to the physical result. Any rounding correction earns the same shell and medium proof as motion.</summary>
internal static class ContactHorizontalRounding
{
    internal static ContactController.ContactTickContinuation Accept(ContactController.ContactTickContinuation tick,
        Vector2 drive, double spent, bool onPlan, in MoveTuning tuning)
    {
        if (!onPlan) return tick with { UnroundedX = tick.Feet.X, UnroundedZ = tick.Feet.Z };
        double x = tick.UnroundedX + drive.X * spent, z = tick.UnroundedZ + drive.Y * spent;
        Vector3 correction = new((float)x - tick.Feet.X, 0, (float)z - tick.Feet.Z);
        if (tick.FluidVisited && correction != Vector3.Zero)
        {
            var velocity = new Vector3(tick.HorizontalVelocity.X, 0, tick.HorizontalVelocity.Y);
            ShellFlight flight = ShellMotion.FlyWithQueries(tick.Queries, tick.Feet, correction, ref velocity,
                tuning, System.MathF.Cos(tick.TractionGate));
            tick.Queries.AcceptRoute(flight.Blocked);
            tick = tick with
            {
                Feet = tick.Feet + flight.Achieved,
                HorizontalVelocity = new(velocity.X, velocity.Z),
                Last = tick.Last with { Achieved = tick.Last.Achieved + new Vector2(flight.Achieved.X, flight.Achieved.Z) },
            };
            if (flight.Blocked || flight.Achieved != correction) (x, z) = (tick.Feet.X, tick.Feet.Z);
        }
        return tick with { UnroundedX = x, UnroundedZ = z };
    }
}
