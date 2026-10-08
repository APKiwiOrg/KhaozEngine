using System;
using System.Buffers;
using System.Numerics;

namespace KhaozEngine.Locomotion;

/// <summary>Consumes complete medium coverage for an accepted solid segment. Surface admission
/// uses SM1's certified upper-kind partition and exact level agreement, never a sampled depth.</summary>
internal static class MovementWaterPathProof
{
    internal static MovementAvailability Check(in MovementBodyQuery enclosure, Vector3 delta,
        MovementQueryLease queries, WaterTraversalMode mode)
    {
        MovementCoverageSpan[] spans = ArrayPool<MovementCoverageSpan>.Shared.Rent(MovementQueryLease.MaxCoverageSpans);
        MovementDomainContact[]? contacts = null;
        try
        {
            contacts = ArrayPool<MovementDomainContact>.Shared.Rent(MovementQueryLease.MaxDomainContacts);
            MovementCoverageResult result = queries.TraceWater(new(enclosure, delta),
                spans.AsSpan(0, MovementQueryLease.MaxCoverageSpans),
                contacts.AsSpan(0, MovementQueryLease.MaxDomainContacts));
            if (result.Availability != MovementAvailability.Known) return result.Availability;
            if (result.ContactsWritten == 0)
            {
                foreach (MovementCoverageSpan span in spans.AsSpan(0, result.SpansWritten))
                    if (!span.HasDryCoverage) return MovementAvailability.Invalid;
                return MovementAvailability.Known;
            }
            if (mode != WaterTraversalMode.SurfaceSwimmer) return MovementAvailability.Unresolved;
            float level = contacts[0].Interval.NominalSurfaceY;
            foreach (MovementDomainContact contact in contacts.AsSpan(0, result.ContactsWritten))
                if (!contact.Interval.UpperIsFreeSurface || contact.Interval.NominalSurfaceY != level)
                    return MovementAvailability.Unresolved;
            return MovementAvailability.Known;
        }
        finally
        {
            if (contacts is not null) ArrayPool<MovementDomainContact>.Shared.Return(contacts, clearArray: true);
            ArrayPool<MovementCoverageSpan>.Shared.Return(spans, clearArray: true);
        }
    }
}
