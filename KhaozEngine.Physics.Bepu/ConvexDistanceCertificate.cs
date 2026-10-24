using Q = KhaozEngine.Physics.Bepu.ConvexQuadratic;
using P = KhaozEngine.Physics.Bepu.ConvexExactPoint;

namespace KhaozEngine.Physics.Bepu;

/// <summary>A query-private attained base pair, retained before margin clipping and encoding.</summary>
internal readonly struct ConvexDistanceCertificate
{
    readonly ConvexQueryScratch? _scratch;
    readonly int _generation;
    internal ConvexQueryRequest Request { get; }
    internal ConvexPlanarSource Source { get; }
    internal ConvexCertificateFraction Fraction { get; }
    internal P Translation { get; }
    internal P Feet => P.From(Request.Pose.Position) + P.From(Request.Displacement) * Fraction.Value + Translation;
    readonly P _query, _target, _normal;
    internal P Query => IsCircular ? throw new ConvexExactRefusalException() : _query;
    internal P Target => IsCircular ? throw new ConvexExactRefusalException() : _target;
    internal P TowardTarget => IsCircular ? throw new ConvexExactRefusalException() : _normal;
    internal GeometryVector QueryBounds => Circular?.QueryBounds ?? _query.Bounds;
    internal GeometryVector TargetBounds => Circular?.TargetBounds ?? _target.Bounds;
    internal GeometryVector NormalBounds => Circular?.NormalBounds ?? _normal.Bounds;
    readonly Q _squared;
    internal ConvexCircularCertificate? Circular { get; }
    internal bool IsCircular => Circular is not null;
    internal Q BaseSquaredDistance => IsCircular ? throw new ConvexExactRefusalException() : _squared;
    internal GeometryInterval SquaredDistanceBounds => Circular?.SquaredDistanceBounds ?? _squared.Bounds;
    internal bool BaseOverlap { get; }
    internal ConvexShellFeature QueryFeature { get; }
    internal ConvexTargetFeature TargetFeature { get; }
    internal int LocalFeatureId { get; }
    internal bool IsAlive => _scratch is not null && _generation == _scratch.CertificateGeneration && Source.IsValid && (Circular is null || Circular.Root.IsAlive);
    internal bool IsOwnedBy(ConvexQueryScratch scratch) => IsAlive && ReferenceEquals(scratch, _scratch);

    internal ConvexDistanceCertificate(in ConvexDistanceContext context, P q, P t, ConvexTargetFeature feature, int id, bool overlap)
    {
        _scratch = context.Scratch; _generation = _scratch.CertificateGeneration;
        Request = context.Request; Source = context.Source; Fraction = context.Fraction;
        Translation = context.Translation;
        _query = q; _target = t; _normal = t - q; _squared = P.Dot(_normal, _normal);
        BaseOverlap = overlap; QueryFeature = overlap ? ConvexShellFeature.Interior : ConvexPlanarFeatures.Classify(Request.Shape, q);
        TargetFeature = feature; LocalFeatureId = id;
    }

    internal ConvexDistanceCertificate(ConvexQueryRequest request, ConvexPlanarSource source, ConvexCertificateFraction fraction,
        ConvexQueryScratch scratch, ConvexCircularCertificate circular, ConvexTargetFeature targetFeature = ConvexTargetFeature.Edge, int? localFeatureId = null)
    {
        _scratch = scratch; _generation = scratch.CertificateGeneration;
        Request = request; Source = source; Fraction = fraction; Circular = circular;
        Translation = circular.Context.Translation;
        QueryFeature = circular.Feature; TargetFeature = targetFeature; LocalFeatureId = localFeatureId ?? circular.LocalFeatureId;
    }

    internal bool TryBarycentricFraction(double offset, double scale, ref ConvexQueryWork work, ConvexQueryScratch scratch,
        out ConvexCertificateFraction fraction)
    {
        fraction = default;
        if (!IsAlive || Circular is null || !double.IsFinite(offset) || !double.IsFinite(scale)) return false;
        var candidate = ConvexCertificateFraction.FromCircular(Circular, offset, scale);
        if (!candidate.TryCompare(ConvexCertificateFraction.From(0), ref work, scratch, out int lower) || lower < 0 ||
            !candidate.TryCompare(ConvexCertificateFraction.From(1), ref work, scratch, out int upper) || upper > 0) return false;
        fraction = candidate;
        return true;
    }

    internal bool TryCompareSquaredMargin(float margin, ref ConvexQueryWork work, ConvexQueryScratch scratch, out int sign)
    {
        sign = 0;
        if (!IsAlive || !ReferenceEquals(scratch, _scratch) || (!float.IsFinite(margin) || margin < 0) || !work.Feature()) return false;
        if (Circular is not null) return Circular.TryCompareSquaredMargin(margin, ref work, scratch, out sign);
        try { sign = (BaseSquaredDistance - Q.From(margin) * Q.From(margin)).Sign; return true; }
        catch (ConvexExactRefusalException) { return work.Refuse(); }
    }

    internal bool TryCompareDistance(in ConvexDistanceCertificate other, ref ConvexQueryWork work,
        ConvexQueryScratch scratch, out int sign)
    {
        sign = 0;
        if (!IsOwnedBy(scratch) || !other.IsOwnedBy(scratch) || !work.Feature()) return false;
        var a = SquaredDistanceBounds; var b = other.SquaredDistanceBounds;
        if (a.IsResolved && b.IsResolved)
        {
            if (a.Upper < b.Lower) { sign = -1; return true; }
            if (a.Lower > b.Upper) { sign = 1; return true; }
        }
        if (!IsCircular && !other.IsCircular) { sign = (BaseSquaredDistance - other.BaseSquaredDistance).Sign; return true; }
        if (IsCircular && !other.IsCircular && other.BaseSquaredDistance.Rational)
            return Circular!.TryCompareSquared(other.BaseSquaredDistance, ref work, scratch, out sign);
        if (!IsCircular && other.IsCircular && BaseSquaredDistance.Rational &&
            other.Circular!.TryCompareSquared(BaseSquaredDistance, ref work, scratch, out sign))
        { sign = -sign; return true; }
        return false;
    }

    internal bool TryOriginal(in ConvexQueryRequest request, in ConvexPlanarSource source, ConvexCertificateFraction time,
        ref ConvexQueryWork work, ConvexQueryScratch scratch, out ConvexDistanceCertificate pair)
    {
        pair = default;
        if (!IsOwnedBy(scratch) || !source.IsOriginal || !source.IsValid || !Request.Equals(request) ||
            !ReferenceEquals(source.Leaf.Geometry, Source.Leaf.Geometry)) return false;
        if (Circular is not null) return Circular.TryAtFraction(request, source, time, ref work, scratch, out pair);
        if (time.Root is not null || !work.Cell() || !work.Feature()) return false;
        P feet = P.From(request.Pose.Position) + P.From(request.Displacement) * time.Value;
        if (!ConvexPlanarFeatures.Member(request.Shape, Query)) return false;
        for (int face = 0; face < source.FaceCount; face++) if (!work.Feature()) return false;
        if (!ConvexPlanarFeatures.InTarget(source, feet, Target)) return false;
        if (BaseSquaredDistance.Sign > 0 && (!ConvexPlanarFeatures.NormalCone(request.Shape, Query, TowardTarget) ||
            !ConvexPlanarFeatures.SupportsTarget(source, feet, Target, TowardTarget, ref work))) return false;
        if (!ConvexPlanarContacts.TryLocalFeature(source, feet, Target, ref work, out var feature, out int id))
        { feature = ConvexTargetFeature.Interior; id = source.FaceCount + source.EdgeCount + source.VertexCount; }
        if (work.Exhausted) return false;
        var context = new ConvexDistanceContext(request, source, time, scratch, false);
        pair = new(context, Query, Target, feature, id, BaseOverlap);
        return true;
    }
}

internal readonly record struct ConvexStaticResult(ConvexLeafEvidence Evidence, ConvexDistanceCertificate Certificate);

/// <summary>The same feature enumeration serves legacy encoding and retained certificates.
/// The legacy accept path calls the original encoder without changing its operand order.</summary>
internal readonly struct ConvexDistanceContext(ConvexQueryRequest request, ConvexPlanarSource source,
    ConvexCertificateFraction fraction, ConvexQueryScratch scratch, bool encode, P translation = default)
{
    internal ConvexQueryRequest Request => request;
    internal ConvexPlanarSource Source => source;
    internal ConvexCertificateFraction Fraction => fraction;
    internal ConvexQueryScratch Scratch => scratch;
    internal bool Encode => encode;
    internal P Translation => translation;
    internal P Feet => P.From(request.Pose.Position) + P.From(request.Displacement) * fraction.Value + translation;
    internal bool TryAccept(P q, P t, ConvexTargetFeature feature, int id, bool overlap, out ConvexStaticResult result)
    {
        result = default;
        if (encode)
        {
            if (!ConvexDistanceEncoding.TryCreate(request, q, t, feature, id, overlap, out var evidence)) return false;
            result = new(evidence, default);
        }
        else result = new(default, new(this, q, t, feature, id, overlap));
        return true;
    }
}
