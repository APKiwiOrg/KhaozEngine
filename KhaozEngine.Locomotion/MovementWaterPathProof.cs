using System;
using System.Buffers;
using System.Numerics;

namespace KhaozEngine.Locomotion;

/// <summary>Consumes complete medium coverage for an accepted solid segment. Local column facts
/// cannot authorize varying wet regions. Until their producer certificate is available, those
/// paths refuse atomically. Known dry coverage can already certify actual movement.</summary>
internal static class MovementWaterPathProof
{
    internal static MovementAvailability Check(in MovementBodyQuery enclosure, Vector3 delta,
        MovementQueryLease queries)
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
            if (result.ContactsWritten != 0) return MovementAvailability.Unresolved;
            foreach (MovementCoverageSpan span in spans.AsSpan(0, result.SpansWritten))
                if (!span.HasDryCoverage || span.ContactCount != 0) return MovementAvailability.Unresolved;
            return MovementAvailability.Known;
        }
        finally
        {
            if (contacts is not null) ArrayPool<MovementDomainContact>.Shared.Return(contacts, clearArray: true);
            ArrayPool<MovementCoverageSpan>.Shared.Return(spans, clearArray: true);
        }
    }
}
