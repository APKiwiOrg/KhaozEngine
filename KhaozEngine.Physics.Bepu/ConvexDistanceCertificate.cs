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
        _query = q; _target = t; _normal = t - q; _squared = P.Dot(_normal, _normal);
        BaseOverlap = overlap; QueryFeature = overlap ? ConvexShellFeature.Interior : ConvexPlanarFeatures.Classify(Request.Shape, q);
        TargetFeature = feature; LocalFeatureId = id;
    }

    internal ConvexDistanceCertificate(ConvexQueryRequest request, ConvexPlanarSource source, ConvexCertificateFraction fraction,
        ConvexQueryScratch scratch, ConvexCircularCertificate circular, ConvexTargetFeature targetFeature = ConvexTargetFeature.Edge, int? localFeatureId = null)
    {
        _scratch = scratch; _generation = scratch.CertificateGeneration;
        Request = request; Source = source; Fraction = fraction; Circular = circular;
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
}

internal readonly record struct ConvexStaticResult(ConvexLeafEvidence Evidence, ConvexDistanceCertificate Certificate);

/// <summary>The same feature enumeration serves legacy encoding and retained certificates.
/// The legacy accept path calls the original encoder without changing its operand order.</summary>
internal readonly struct ConvexDistanceContext(ConvexQueryRequest request, ConvexPlanarSource source,
    ConvexCertificateFraction fraction, ConvexQueryScratch scratch, bool encode)
{
    internal ConvexQueryRequest Request => request;
    internal ConvexPlanarSource Source => source;
    internal ConvexCertificateFraction Fraction => fraction;
    internal ConvexQueryScratch Scratch => scratch;
    internal bool Encode => encode;
    internal P Feet => P.From(request.Pose.Position) + P.From(request.Displacement) * fraction.Value;
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
