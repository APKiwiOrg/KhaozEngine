using System;
using System.Numerics;

namespace KhaozEngine.Locomotion;

public sealed partial class MovementQueryLease
{
    /// <summary>Distinct level bodies observable during one bounded read interval, not a world-size limit.</summary>
    public const int MaxWaterDomainsPerLease = 256;

    MovementAvailability RememberWaterPoint(in MovementWaterPoint point)
    {
        if (!point.InWater) return MovementAvailability.Known;
        MovementDomainKey key = point.Domain!.Value;
        float level = point.Interval!.Value.NominalSurfaceY;
        return Workspace.RememberWater(key, level);
    }

    MovementAvailability RememberWaterContacts(ReadOnlySpan<MovementDomainContact> contacts)
    {
        int added = 0;
        for (int i = 0; i < contacts.Length; i++)
        {
            MovementDomainContact current = contacts[i];
            float level = current.Interval.NominalSurfaceY;
            bool known = false;
            if (Workspace.TryGetWater(current.Domain, out float value))
            {
                known = true;
                if (value != level) return MovementAvailability.Invalid;
            }
            bool duplicate = false;
            for (int j = 0; j < i; j++)
            {
                if (contacts[j].Domain != current.Domain) continue;
                if (contacts[j].Interval.NominalSurfaceY != level) return MovementAvailability.Invalid;
                duplicate = true;
                break;
            }
            if (!known && !duplicate) added++;
        }
        if (Workspace.WaterCount + added > MaxWaterDomainsPerLease) return MovementAvailability.CapacityExceeded;
        // Commit facts only after the entire result, including its located-column checks, passed.
        if (added > 0)
        {
            foreach (MovementDomainContact contact in contacts)
                Workspace.RememberWater(contact.Domain, contact.Interval.NominalSurfaceY);
        }
        return MovementAvailability.Known;
    }

    static bool TangentCoherent(in MovementMediumSweepQuery query, in MovementDomainContact contact, float error)
    {
        if (contact.Overlap != MovementContactOverlap.Tangent) return true;
        double radius = (double)query.Body.Radius - error;
        if (radius <= 0) return true;
        double x = query.Body.Centre.X + (double)contact.Fraction * query.Delta.X;
        double y = query.Body.Centre.Y + (double)contact.Fraction * query.Delta.Y;
        double z = query.Body.Centre.Z + (double)contact.Fraction * query.Delta.Z;
        Vector2 column = contact.IntervalColumnXZ;
        double dx = column.X - x, dz = column.Y - z;
        double capSquared = radius * radius - dx * dx - dz * dz;
        if (capSquared <= 0) return true;
        double cap = Math.Sqrt(capSquared);
        double core = (double)query.Body.HalfHeight - query.Body.Radius;
        double low = Math.Max(y - core - cap, (double)contact.Interval.LowerY + error);
        double high = Math.Min(y + core + cap, (double)contact.Interval.UpperY - error);
        return high <= low;
    }
}
