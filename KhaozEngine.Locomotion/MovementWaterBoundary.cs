using System;
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
        Span<Vector3> normals, out int normalCount, out Hit hit, float proximity = 0) =>
        Find(enclosure, delta, bodyHalfHeight, enterFraction, mode, queries, normals, out normalCount, out hit,
            proximity, certify: true, out _);

    /// <summary>The water boundary parity suite's seam. It answers the same question on the same lease with the
    /// capture certificate allowed or with the full solve only, and reports whether the certificate answered.
    /// Movement code calls <see cref="Find(in MovementBodyQuery, Vector3, float, float, WaterTraversalMode,
    /// MovementQueryLease, Span{Vector3}, out int, out Hit, float)"/>.</summary>
    internal static MovementAvailability FindForParityTests(in MovementBodyQuery enclosure, Vector3 delta,
        float bodyHalfHeight, float enterFraction, WaterTraversalMode mode, MovementQueryLease queries,
        Span<Vector3> normals, out int normalCount, out Hit hit, float proximity, bool certify, out bool certified) =>
        Find(enclosure, delta, bodyHalfHeight, enterFraction, mode, queries, normals, out normalCount, out hit,
            proximity, certify, out certified);

    static MovementAvailability Find(in MovementBodyQuery enclosure, Vector3 delta,
        float bodyHalfHeight, float enterFraction, WaterTraversalMode mode, MovementQueryLease queries,
        Span<Vector3> normals, out int normalCount, out Hit hit, float proximity, bool certify, out bool certified)
    {
        normalCount = 0;
        hit = default;
        certified = false;
        if (mode is not (WaterTraversalMode.SurfaceSwimmer or WaterTraversalMode.WadeOnly or WaterTraversalMode.DryOnly))
            return MovementAvailability.Invalid;
        var query = new MovementMediumSweepQuery(enclosure, delta);
        // Before any coverage buffer is borrowed. A certified answer is the one every Known full solve gives here.
        // Navigation bake leases always take the full solve, see MovementWaterBoundaryScope.
        if (certify && queries.WaterBoundaryScope == MovementWaterBoundaryScope.Runtime &&
            MovementWaterBoundaryCertificate.Answers(query, bodyHalfHeight, enterFraction, mode, queries, proximity))
        {
            certified = true;
            return MovementAvailability.Known;
        }
        var storage = queries.BorrowWaterCoverage(boundary: true);
        Span<MovementCoverageSpan> spans = storage.Spans;
        Span<MovementDomainContact> contacts = storage.Contacts;
        int initialized = 0;
        try
        {
            MovementCoverageResult result = queries.TraceWater(query,
                spans, contacts);
            initialized = result.ContactsWritten;
            if (result.Availability != MovementAvailability.Known) return result.Availability;
            Span<Candidate> candidates = stackalloc Candidate[MovementQueryLease.MaxDomainContacts];
            int count = 0;
            double first = double.PositiveInfinity;
            bool overlap = false;
            foreach (MovementCoverageSpan span in spans[..result.SpansWritten])
            {
                ReadOnlySpan<MovementDomainContact> region = contacts.Slice(span.ContactStart, span.ContactCount);
                if (mode == WaterTraversalMode.SurfaceSwimmer)
                {
                    float? level = null;
                    foreach (MovementDomainContact contact in region)
                    {
                        if (!contact.Interval.UpperIsFreeSurface) continue;
                        if (level is { } previous && previous != contact.Interval.NominalSurfaceY)
                            return MovementAvailability.Unresolved;
                        level = contact.Interval.NominalSurfaceY;
                    }
                }
                foreach (MovementDomainContact contact in region)
                {
                    if (mode == WaterTraversalMode.SurfaceSwimmer && contact.Interval.UpperIsFreeSurface) continue;
                    if (contact.SpanOverlap == MovementContactOverlap.Tangent) continue;
                    double at = span.EnterFraction;
                    Vector3 normal = contact.Normal;
                    if (mode == WaterTraversalMode.WadeOnly && contact.Interval.UpperIsFreeSurface)
                    {
                        double threshold = WadeThreshold(contact.Interval.NominalSurfaceY, bodyHalfHeight, enterFraction,
                            result.CertifiedErrorMetres, proximity);
                        double startY = PathCentreY(enclosure.Centre.Y, delta.Y, span.EnterFraction);
                        double endY = PathCentreY(enclosure.Centre.Y, delta.Y, span.ExitFraction);
                        if (Math.Min(startY, endY) > threshold) continue;
                        if (startY > threshold)
                        {
                            at = (threshold - enclosure.Centre.Y) / delta.Y;
                            normal = Vector3.UnitY;
                        }
                    }
                    if ((delta == Vector3.Zero || at == 0 && contact.Fraction == 0) && contact.Overlap == MovementContactOverlap.Overlapping)
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
            storage.Return(initialized);
        }
    }

    /// <summary>The centre height a WadeOnly body must stay above for a free-surface contact to be skipped: the level
    /// plus the body's offset at its enter line, then the certified error and proximity as one binary32 sum.
    /// <see cref="Find(in MovementBodyQuery, Vector3, float, float, WaterTraversalMode, MovementQueryLease,
    /// Span{Vector3}, out int, out Hit, float)"/> and the wade-depth certificate both use it, so the two cannot drift.
    /// Every rounding mode keeps it monotone in the error.</summary>
    internal static double WadeThreshold(float surfaceY, float bodyHalfHeight, float enterFraction, float certifiedError,
        float proximity)
    {
        double threshold = surfaceY + bodyHalfHeight * (1d - 2d * enterFraction);
        return threshold + (certifiedError + proximity);
    }

    /// <summary>The path centre's height at a fraction. The binary32 product is exact in binary64.</summary>
    internal static double PathCentreY(float centreY, float deltaY, float fraction) => centreY + deltaY * (double)fraction;
}
