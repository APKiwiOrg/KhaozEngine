using System;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion.Contacts;

/// <summary>What the down pass decided at a new axis.</summary>
internal enum SeatOutcome : byte { Seated, SteepSeated, Airborne, Wall, Refused }

/// <summary>The down pass result. <see cref="FeetY"/> is the seated height, or the start feet when the body is
/// not seated. <see cref="StepPart"/> is the part of a walkable rise the start support's plane does not explain.
/// <see cref="WallNormal"/> is a horizontal unit vector for <see cref="SeatOutcome.Wall"/>, else zero.</summary>
internal readonly record struct GroundSeatResult(SeatOutcome Outcome, float FeetY, SupportSample Support,
    float StepPart, Vector3 WallNormal, float DiscreteStep = 0);

/// <summary>Seats a body within the legs band above its feet and the drop band below its start support.</summary>
internal static class GroundSeat
{
    static readonly SupportSample NoSupport =
        new(SupportStatus.None, float.NaN, float.NaN, Vector3.Zero, null, -1, Vector3.Zero);

    /// <summary>Applies the seat rules in order. Analytic terrain more than <see cref="MoveTuning.StepHeight"/>
    /// above the start feet is a wall. Otherwise walkable support seats, steep support above the start feet is a
    /// wall, steep support at or below seats with steep footing, no support leaves the body airborne at the start
    /// height and a refusal refuses. <paramref name="start"/> is the support at <paramref name="startAxis"/>.
    /// <paramref name="moveDirection"/> must be finite and non-zero. <paramref name="cosMaxSlope"/> is the cosine of
    /// the tick's traction gate. The <paramref name="queries"/> context enforces the <see cref="FootSupport.Find"/>
    /// contract.</summary>
    internal static GroundSeatResult Resolve(ContactQueryContext queries,
        in SupportSample start, Vector2 startAxis, float startFeetY, Vector2 axis, Vector2 moveDirection,
        float footRadius, in MoveTuning tuning, float cosMaxSlope)
    {
        if (!float.IsFinite(moveDirection.X) || !float.IsFinite(moveDirection.Y) || moveDirection == Vector2.Zero)
            throw new ArgumentOutOfRangeException(nameof(moveDirection),
                "The move direction must be finite and non-zero.");
        float stepHeight = tuning.StepHeight;
        if (queries.HeightAt(new(axis.X, startFeetY, axis.Y)) > startFeetY + stepHeight)
        {
            Vector3 normal = queries.NormalAt(new(axis.X, startFeetY, axis.Y)) ?? Vector3.Zero;
            return Wall(startFeetY, NoSupport, normal, moveDirection);
        }

        FootSupportQuery query = Query(axis, start, startFeetY, footRadius, tuning, cosMaxSlope);
        SupportSample support = queries.FindSupport(query);
        switch (support.Status)
        {
            case SupportStatus.Walkable:
                return new(SeatOutcome.Seated, support.Height, support,
                    StepPart(support.Height, start, startAxis, startFeetY, axis), Vector3.Zero,
                    DiscreteStep(start, startAxis, support, axis));
            case SupportStatus.Steep:
                // A footed body never climbs ground it cannot stand on, so a face that may rise above the start
                // feet is a wall.
                if ((double)support.Height - support.HeightError > startFeetY)
                    return Wall(startFeetY, support, support.Normal, moveDirection);
                return new(SeatOutcome.SteepSeated, support.Height, support, 0, Vector3.Zero);
            case SupportStatus.None:
                return new(SeatOutcome.Airborne, startFeetY, support, 0, Vector3.Zero);
            default:
                return new(SeatOutcome.Refused, startFeetY, support, 0, Vector3.Zero);
        }
    }

    // Drops are measured between supports. The upper limit stays at the legs band above the paced feet.
    internal static FootSupportQuery Query(Vector2 axis, in SupportSample start, float startFeetY,
        float footRadius, in MoveTuning tuning, float cosMaxSlope)
    {
        float referenceY = start.Status == SupportStatus.Walkable ? start.Height : startFeetY;
        float reachDown = (float)((double)startFeetY - referenceY + tuning.StepHeight);
        return new(axis, startFeetY, footRadius, tuning.StepHeight, reachDown, cosMaxSlope);
    }

    // The seated height less the start support's plane extrapolated to the new axis. Without a start plane the
    // reference is level at the start feet. A steep start, which only a slide moves from, explains no walkable rise,
    // so the part is the rise above the start feet and a drop charges nothing. The part is charged against the
    // climb budget, so it rounds up and a paid climb never exceeds the budget.
    static float StepPart(float height, in SupportSample start, Vector2 startAxis, float startFeetY, Vector2 axis)
    {
        if (start.Status == SupportStatus.Steep)
            return MathF.Max(0f, GroundPlacement.NotBelow((double)height - startFeetY));
        Vector3 n = start.Normal;
        bool hasPlane = start.Status == SupportStatus.Walkable && n.Y > 0;
        if (!hasPlane) return GroundPlacement.NotBelow((double)height - startFeetY);
        double plane = start.Height + ((double)n.X * ((double)startAxis.X - axis.X) +
            (double)n.Z * ((double)startAxis.Y - axis.Y)) / n.Y;
        return GroundPlacement.NotBelow(height - plane);
    }

    // A height jump belongs to a step only when both support planes agree on its direction and clear the
    // contact skin plus certified height errors. Opposite signs identify continuous ground.
    internal static float DiscreteStep(in SupportSample start, Vector2 startAxis, in SupportSample next,
        Vector2 axis)
    {
        if (start.Status != SupportStatus.Walkable || next.Status != SupportStatus.Walkable) return 0;
        double dx = (double)axis.X - startAxis.X, dz = (double)axis.Y - startAxis.Y;
        double forward = next.Height - (start.Height - PlaneRise(start, startAxis, dx, dz));
        double backward = next.Height + PlaneRise(next, axis, dx, dz) - start.Height;
        double error = (double)ShellMotion.ContactSkin + start.HeightError + next.HeightError;
        if (Math.Abs(forward) <= error || Math.Abs(backward) <= error ||
            Math.Sign(forward) != Math.Sign(backward)) return 0;
        return (float)(Math.Sign(forward) * Math.Min(Math.Abs(forward), Math.Abs(backward)));
    }

    // The rise of a support's plane over (dx, dz). A certified height is the least of the support's own plane at the
    // axis, its witness height and its joined polygons' planes. A height below its own plane at its axis by more than
    // the skin plus its error came from the witness, an edge or vertex the footprint overhangs, so the height field
    // there is level even though the normal is the face's.
    static double PlaneRise(in SupportSample support, Vector2 supportAxis, double dx, double dz)
    {
        Vector3 n = support.Normal, w = support.Witness;
        double plane = w.Y - ((double)n.X * ((double)supportAxis.X - w.X) +
            (double)n.Z * ((double)supportAxis.Y - w.Z)) / n.Y;
        if (plane - support.Height > (double)ShellMotion.ContactSkin + support.HeightError) return 0;
        return ((double)n.X * dx + (double)n.Z * dz) / n.Y;
    }

    // The wall normal is the contact normal made horizontal, or the reverse of the move when it has no
    // horizontal part.
    static GroundSeatResult Wall(float startFeetY, in SupportSample support, Vector3 normal, Vector2 moveDirection)
    {
        var horizontal = new Vector3(normal.X, 0, normal.Z);
        float length = horizontal.Length();
        Vector3 wall = length > 0 && float.IsFinite(length)
            ? horizontal / length
            : Vector3.Normalize(new Vector3(-moveDirection.X, 0, -moveDirection.Y));
        return new(SeatOutcome.Wall, startFeetY, support, 0, wall);
    }
}
