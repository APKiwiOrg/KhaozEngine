using System;
using System.Buffers;
using System.Numerics;

namespace KhaozEngine.Locomotion;

/// <summary>Proposes forbidden-medium constraints from complete coverage. Distances are candidates,
/// not clear-prefix certificates. The shared resolver must retrace every proposed segment.</summary>
internal static class MovementWaterBoundary
{
    internal readonly record struct Hit(bool Blocked, double Distance, bool InitialOverlap);
    readonly record struct Candidate(double Fraction, Vector3 Normal);

    internal static MovementAvailability Find(in MovementBodyQuery enclosure, Vector3 delta,
        float bodyHalfHeight, float enterFraction, WaterTraversalMode mode, MovementQueryLease queries,
        Span<Vector3> normals, out int normalCount, out Hit hit, float proximity = 0)
    {
        normalCount = 0;
        hit = default;
        if (mode is not (WaterTraversalMode.WadeOnly or WaterTraversalMode.DryOnly))
            return MovementAvailability.Invalid;
        MovementCoverageSpan[] spans = ArrayPool<MovementCoverageSpan>.Shared.Rent(MovementQueryLease.MaxCoverageSpans);
        MovementDomainContact[] contacts = ArrayPool<MovementDomainContact>.Shared.Rent(MovementQueryLease.MaxDomainContacts);
        try
        {
            MovementCoverageResult result = queries.TraceWater(new(enclosure, delta),
                spans.AsSpan(0, MovementQueryLease.MaxCoverageSpans), contacts.AsSpan(0, MovementQueryLease.MaxDomainContacts));
            if (result.Availability != MovementAvailability.Known) return result.Availability;
            Span<Candidate> candidates = stackalloc Candidate[MovementQueryLease.MaxDomainContacts];
            int count = 0;
            double first = double.PositiveInfinity;
            bool overlap = false;
            foreach (MovementCoverageSpan span in spans.AsSpan(0, result.SpansWritten))
            {
                foreach (MovementDomainContact contact in contacts.AsSpan(span.ContactStart, span.ContactCount))
                {
                    if (contact.SpanOverlap == MovementContactOverlap.Tangent) continue;
                    double at = span.EnterFraction;
                    Vector3 normal = contact.Normal;
                    if (mode == WaterTraversalMode.WadeOnly && contact.Interval.UpperIsFreeSurface)
                    {
                        double threshold = contact.Interval.NominalSurfaceY + bodyHalfHeight * (1d - 2d * enterFraction);
                        double startY = enclosure.Centre.Y + delta.Y * (double)span.EnterFraction;
                        double endY = enclosure.Centre.Y + delta.Y * (double)span.ExitFraction;
                        threshold += result.CertifiedErrorMetres + proximity;
                        if (Math.Min(startY, endY) > threshold) continue;
                        if (startY > threshold)
                        {
                            at = (threshold - enclosure.Centre.Y) / delta.Y;
                            normal = Vector3.UnitY;
                        }
                    }
                    if (at == 0 && contact.Fraction == 0 && contact.Overlap == MovementContactOverlap.Overlapping)
                        overlap = true;
                    // A zero-length placement can use the point certificate. A finite path cannot
                    // extrapolate this tag to the rest of a curved region.
                    if (delta == Vector3.Zero && !overlap && contact.Fraction == 0 &&
                        contact.Overlap == MovementContactOverlap.Tangent && proximity == 0) continue;
                    if (count == candidates.Length) return MovementAvailability.CapacityExceeded;
                    candidates[count++] = new(at, normal);
                    first = Math.Min(first, at);
                }
            }
            if (count == 0) return MovementAvailability.Known;
            double length = Math.Sqrt((double)delta.X * delta.X + (double)delta.Y * delta.Y + (double)delta.Z * delta.Z);
            foreach (Candidate candidate in candidates[..count])
            {
                if ((candidate.Fraction - first) * length > MovementQueryLease.CoverageSkinMetres + result.CertifiedErrorMetres)
                    continue;
                bool duplicate = false;
                foreach (Vector3 existing in normals[..normalCount])
                    if (existing == candidate.Normal) { duplicate = true; break; }
                if (duplicate) continue;
                if (normalCount == normals.Length) return MovementAvailability.CapacityExceeded;
                normals[normalCount++] = candidate.Normal;
            }
            hit = new(true, first * length, overlap);
            return MovementAvailability.Known;
        }
        finally
        {
            ArrayPool<MovementCoverageSpan>.Shared.Return(spans, clearArray: true);
            ArrayPool<MovementDomainContact>.Shared.Return(contacts, clearArray: true);
        }
    }
}
