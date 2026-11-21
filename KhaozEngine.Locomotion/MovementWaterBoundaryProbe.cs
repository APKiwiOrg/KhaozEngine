#if CONTACT_SHORE_ATTRIBUTION
using System;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion;

// Count-pass observer for MovementWaterBoundary.Find. Reads the coverage and the derived answer after the
// released code has decided them, and evaluates two proposed capture certificates beside it.
internal static class MovementWaterBoundaryProbe
{
    // The certificate consultation in its own scope, so its time is not charged to the boundary proof around it.
    internal static bool Certify(in MovementMediumSweepQuery query, float bodyHalfHeight, float enterFraction,
        WaterTraversalMode mode, MovementQueryLease queries, float proximity)
    {
        bool answered;
        using (ShoreStepProbe.Begin(ShorePhase.WaterCertificate))
            answered = MovementWaterBoundaryCertificate.Answers(query, bodyHalfHeight, enterFraction, mode, queries,
                proximity);
        ShoreStepProbe.Count(answered ? ShoreEvent.CertificateAnswer : ShoreEvent.CertificateDecline);
        return answered;
    }

    internal static void Coverage(in MovementCoverageResult result, ReadOnlySpan<MovementCoverageSpan> spans,
        ReadOnlySpan<MovementDomainContact> contacts)
    {
        if (!ShoreProofCounts.Active) return;
        int free = 0, closed = 0, mixed = 0, tangent = 0;
        if (result.Availability == MovementAvailability.Known)
            foreach (MovementCoverageSpan span in spans[..result.SpansWritten])
            {
                float? level = null;
                bool unequal = false;
                foreach (MovementDomainContact contact in contacts.Slice(span.ContactStart, span.ContactCount))
                {
                    if (contact.SpanOverlap == MovementContactOverlap.Tangent) tangent++;
                    if (!contact.Interval.UpperIsFreeSurface) { closed++; continue; }
                    free++;
                    if (level is { } previous && previous != contact.Interval.NominalSurfaceY) unequal = true;
                    level = contact.Interval.NominalSurfaceY;
                }
                if (unequal) mixed++;
            }
        ShoreProofCounts.Coverage(result.Availability.ToString(), result.SpansWritten, result.ContactsWritten,
            free, closed, mixed, tangent, result.CertifiedErrorMetres);
    }

    internal static void Close(MovementAvailability status, in MovementBodyQuery enclosure, Vector3 delta,
        float bodyHalfHeight, float enterFraction, float proximity, int candidates, int normals,
        in MovementWaterBoundary.Hit hit)
    {
        if (!ShoreProofCounts.Active) return;
        ShoreProofCounts.Sampling? sampling = ShoreProofCounts.OpenSampling();
        double? margin = null;
        if (sampling?.MaxSurfaceLocal is { } surface)
        {
            // The WadeOnly threshold with the largest reachable level and the largest certified error the
            // lease admits, against the lowest centre height any span fraction can produce.
            double threshold = surface + bodyHalfHeight * (1d - 2d * enterFraction);
            threshold += MovementQueryLease.CoverageSkinMetres + proximity;
            double lowest = enclosure.Centre.Y + Math.Min(0d, (double)delta.Y);
            margin = lowest - threshold;
        }
        bool reached = sampling is { Columns: >= 0 };
        bool wade = reached && (sampling!.Columns == 0 || margin > 0);
        ShoreProofCounts.Close(status.ToString(), candidates, normals, hit.Blocked, hit.InitialOverlap, margin,
            reached && sampling!.LevelCertificate, wade);
    }
}
#endif
