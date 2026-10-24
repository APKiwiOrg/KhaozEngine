using System;
using P = KhaozEngine.Physics.Bepu.ConvexExactPoint;

namespace KhaozEngine.Physics.Bepu;

/// <summary>An attained original-path event and an original-member operational normal.
/// The primal inner pair supplies event existence through its labelled affine identity.
/// Only original touching or penetration evidence supplies the operational normal.</summary>
internal readonly struct ConvexImpactCertificate
{
    readonly ConvexQueryScratch? _scratch;
    readonly int _generation;
    internal ConvexQueryRequest Request { get; }
    internal ConvexPlanarSource Original { get; }
    internal ConvexTemporalAttainment? Attainment { get; }
    internal ConvexTemporalSimplex? Simplex => Attainment?.Simplex;
    // This is primal attainment, never an original normal or local feature identity.
    internal ConvexDistanceCertificate Pair => Attainment?.Pair ?? default;
    internal ConvexDistanceCertificate OriginalPair { get; }
    internal ConvexCertificateFraction Time => Attainment?.Time ?? default;
    internal ConvexCertificateFraction NormalTime { get; }
    internal ConvexPenetrationCertificate Penetration { get; }
    internal ConvexLeafEvidence Contact { get; }
    internal bool IsAlive => _scratch is not null && _generation == _scratch.CertificateGeneration &&
        Original.IsValid && Attainment is { IsAlive: true } && Contact.IsComplete &&
        (Penetration.IsAlive || OriginalPair.IsAlive && !Contact.IsOverlapping);

    ConvexImpactCertificate(ConvexQueryRequest request, ConvexPlanarSource original, ConvexTemporalAttainment attained,
        ConvexDistanceCertificate originalPair, ConvexCertificateFraction normalTime,
        ConvexPenetrationCertificate penetration, ConvexLeafEvidence contact, ConvexQueryScratch scratch)
    {
        Request = request; Original = original; Attainment = attained; OriginalPair = originalPair; NormalTime = normalTime;
        Penetration = penetration; Contact = contact; _scratch = scratch; _generation = scratch.CertificateGeneration;
    }

    internal static bool TryCreate(in ConvexQueryRequest request, in ConvexPlanarSource original,
        ConvexTemporalSimplex simplex, in ConvexDistanceCertificate pair, ConvexCertificateFraction time,
        double from, double to, ref ConvexQueryWork work, ConvexQueryScratch scratch, out ConvexImpactCertificate impact)
    {
        impact = default;
        return simplex.TryAttain(pair, ref work, scratch, out var attained) &&
            attained.Time.TryCompare(time, ref work, scratch, out int same) && same == 0 &&
            TryCreate(request, original, attained, from, to, ref work, scratch, out impact);
    }

    internal static bool TryCreate(in ConvexQueryRequest request, in ConvexPlanarSource original,
        ConvexTemporalAttainment attained, double from, double to, ref ConvexQueryWork work,
        ConvexQueryScratch scratch, out ConvexImpactCertificate impact)
    {
        impact = default;
        var time = attained.Time;
        if (!attained.IsAlive || !attained.Pair.IsOwnedBy(scratch) || !attained.Pair.Request.Equals(request) ||
            !ReferenceEquals(attained.Simplex.Original.Token, original.Token) ||
            !time.TryCompare(ConvexCertificateFraction.From(from), ref work, scratch, out int lower) || lower < 0 ||
            !time.TryCompare(ConvexCertificateFraction.From(to), ref work, scratch, out int upper) || upper > 0 ||
            !attained.Pair.TryCompareSquaredMargin(request.Shape.Margin, ref work, scratch, out int sign) || sign > 0) return false;
        if (attained.Pair.TryOriginal(request, original, time, ref work, scratch, out var pair) &&
            ConvexPlanarContacts.TryAttainedTouch(pair, ref work, scratch, out var touching))
        {
            impact = new(request, original, attained, pair, time, default, touching.Contact, scratch);
            return true;
        }
        if (work.Exhausted || !TryDyadicEndpoint(request, attained, to, sign, ref work, scratch, out var normalTime)) return false;
        // Penetration borrows this generation. The root-defined event and immutable weights
        // must remain alive while the accepted original-member path supplies its normal.
        if (!ConvexPlanarPenetration.TryEvaluate(request, original.Leaf, normalTime, ref work, scratch,
            out var penetration, replace: false)) return false;
        impact = new(request, original, attained, default, normalTime, penetration, penetration.Contact, scratch);
        return true;
    }

    static bool TryDyadicEndpoint(in ConvexQueryRequest request, ConvexTemporalAttainment attained, double to, int sign,
        ref ConvexQueryWork work, ConvexQueryScratch scratch, out ConvexCertificateFraction endpoint)
    {
        endpoint = default;
        var time = attained.Time;
        var length = P.From(request.Displacement).Bounds;
        var speed = length.X.Square().Add(length.Y.Square()).Add(length.Z.Square()).Sqrt();
        while (!work.Exhausted)
        {
            var bounds = time.Bounds;
            if (!bounds.IsResolved) return false;
            double upper = Math.Min(to, bounds.Upper);
            var candidate = ConvexCertificateFraction.From(upper);
            if (!candidate.IsValid || !candidate.TryCompare(time, ref work, scratch, out int order) || order < 0) return false;
            // The strict circular margin gap supplies a neighbourhood of overlapping poses.
            // This Lipschitz bound chooses the dyadic pose, then 2c.2 certifies it independently.
            if (time.Root is null || sign == 0)
            { endpoint = candidate; return true; }
            var distance = attained.Pair.SquaredDistanceBounds.Sqrt();
            var drift = GeometryInterval.Exact(upper).Subtract(bounds).Multiply(speed);
            if (distance.IsResolved && drift.IsResolved &&
                distance.Add(GeometryInterval.Exact(Math.Max(0, drift.Upper))).Upper < request.Shape.Margin)
            { endpoint = candidate; return true; }
            double width = (bounds.Upper - bounds.Lower) / 4;
            if (width <= 0 || !time.TryRefine(width, ref work, scratch, out _)) return false;
        }
        return false;
    }

    internal bool TryValidate(in ConvexQueryRequest request, in InstalledSweepLeaf leaf,
        ref ConvexQueryWork work, ConvexQueryScratch scratch)
    {
        return IsAlive && ReferenceEquals(scratch, _scratch) && Request.Equals(request) && Original.IsOriginal &&
            ReferenceEquals(Original.Leaf.Geometry, leaf.Geometry) && Original.Leaf.Pose.Equals(leaf.Pose) &&
            !work.Exhausted && work.Feature();
    }
}
