using System;
using P = KhaozEngine.Physics.Bepu.ConvexExactPoint;
using Q = KhaozEngine.Physics.Bepu.ConvexQuadratic;

namespace KhaozEngine.Physics.Bepu;

/// <summary>One original vertex at one exact dyadic endpoint, in the original feet frame.</summary>
internal readonly struct ConvexTemporalVertex
{
    readonly object _owner;
    readonly ConvexQueryRequest _request;
    internal int OriginalVertex { get; }
    internal double Endpoint { get; }
    internal P Point { get; }
    ConvexTemporalVertex(in ConvexQueryRequest request, in ConvexPlanarSource source, int vertex, double endpoint)
    {
        _owner = source.Token!; _request = request; OriginalVertex = vertex; Endpoint = endpoint;
        Point = source.Vertex(vertex) - P.From(request.Pose.Position) - P.From(request.Displacement) * Q.From(endpoint);
    }
    internal static ConvexTemporalVertex Create(in ConvexQueryRequest request, in ConvexPlanarSource source, int vertex, double endpoint)
    {
        if (!source.IsOriginal || !source.IsValid || vertex < 0 || vertex >= source.VertexCount ||
            !double.IsFinite(endpoint) || endpoint < 0 || endpoint > 1) throw new ConvexExactRefusalException();
        return new(request, source, vertex, endpoint);
    }
    internal bool Matches(in ConvexQueryRequest request, in ConvexPlanarSource source) =>
        ReferenceEquals(_owner, source.Token) && _request.Equals(request);
    internal bool Same(in ConvexTemporalVertex other) => ReferenceEquals(_owner, other._owner) &&
        OriginalVertex == other.OriginalVertex && Endpoint == other.Endpoint;
}

/// <summary>An inner proposal, never an installed target or a clear certificate. Exact affine
/// membership recovers an attained time. Original-member predicates alone supply its normal.</summary>
internal sealed class ConvexTemporalSimplex
{
    internal ConvexPlanarSource Original { get; }
    internal ConvexPlanarSource Source { get; }
    readonly ConvexTemporalVertex[] _vertices;
    internal int Rank => _vertices.Length - 1;
    ConvexTemporalSimplex(ConvexPlanarSource original, ConvexPlanarSource source, ConvexTemporalVertex[] vertices)
    { Original = original; Source = source; _vertices = vertices; }

    internal static bool TryCreate(in ConvexQueryRequest request, in ConvexPlanarSource source,
        ReadOnlySpan<ConvexTemporalVertex> vertices, out ConvexTemporalSimplex simplex)
    {
        simplex = null!;
        if (!source.TryTemporal(request, vertices, out var inner)) return false;
        simplex = new(source, inner, vertices.ToArray());
        return true;
    }

    internal bool TryCoordinates(P point, ref ConvexQueryWork work, out Q[] weights, out ConvexCertificateFraction time)
    {
        weights = []; time = default;
        if (!work.Cell() || !work.Feature()) return false;
        P a = _vertices[0].Point, delta = point - a;
        Q[] w = new Q[_vertices.Length];
        if (Rank == 1)
        {
            P u = _vertices[1].Point - a;
            w[1] = P.Dot(delta, u) / P.Dot(u, u);
        }
        else if (Rank == 2)
        {
            P u = _vertices[1].Point - a, v = _vertices[2].Point - a;
            Q uu = P.Dot(u, u), uv = P.Dot(u, v), vv = P.Dot(v, v), determinant = uu * vv - uv * uv;
            w[1] = (P.Dot(delta, u) * vv - P.Dot(delta, v) * uv) / determinant;
            w[2] = (P.Dot(delta, v) * uu - P.Dot(delta, u) * uv) / determinant;
        }
        else if (Rank == 3)
        {
            P u = _vertices[1].Point - a, v = _vertices[2].Point - a, z = _vertices[3].Point - a;
            Q determinant = P.Dot(u, P.Cross(v, z));
            w[1] = P.Dot(delta, P.Cross(v, z)) / determinant;
            w[2] = P.Dot(u, P.Cross(delta, z)) / determinant;
            w[3] = P.Dot(u, P.Cross(v, delta)) / determinant;
        }
        w[0] = Q.From(1);
        for (int i = 1; i < w.Length; i++) w[0] -= w[i];
        P attained = default;
        Q tau = default;
        for (int i = 0; i < w.Length; i++)
        {
            if (!work.Feature() || w[i].Sign < 0) return false;
            attained += _vertices[i].Point * w[i];
            tau += Q.From(_vertices[i].Endpoint) * w[i];
        }
        if (attained != point) return false;
        time = ConvexCertificateFraction.From(tau);
        if (!time.IsValid) return false;
        weights = w;
        return true;
    }

    internal bool TryTime(in ConvexDistanceCertificate pair, ref ConvexQueryWork work, ConvexQueryScratch scratch,
        out ConvexCertificateFraction time)
    {
        time = default;
        if (!TryAttain(pair, ref work, scratch, out var attained)) return false;
        time = attained.Time;
        return true;
    }

    internal bool TryAttain(in ConvexDistanceCertificate pair, ref ConvexQueryWork work, ConvexQueryScratch scratch,
        out ConvexTemporalAttainment attained)
    {
        attained = null!;
        if (!pair.IsOwnedBy(scratch) || !ReferenceEquals(pair.Source.Token, Source.Token) ||
            pair.Translation != default || pair.Fraction.Root is not null || pair.Fraction.Value.Sign != 0) return false;
        ConvexCertificateFraction time;
        var weights = new ConvexCertificateFraction[_vertices.Length];
        if (!pair.IsCircular)
        {
            if (!TryCoordinates(pair.Target, ref work, out var exact, out time)) return false;
            for (int i = 0; i < weights.Length; i++) weights[i] = ConvexCertificateFraction.From(exact[i]);
        }
        else
        {
            int edge = pair.LocalFeatureId - Source.FaceCount;
            if (pair.TargetFeature != ConvexTargetFeature.Edge || edge < 0 || edge >= Source.EdgeCount) return false;
            var ends = Source.Edge(edge);
            for (int i = 0; i < weights.Length; i++) weights[i] = ConvexCertificateFraction.FromCircular(pair.Circular!, 0, 0);
            weights[ends.A] = ConvexCertificateFraction.FromCircular(pair.Circular!, 1, -1);
            weights[ends.B] = ConvexCertificateFraction.FromCircular(pair.Circular!, 0, 1);
            var a = Q.From(_vertices[ends.A].Endpoint);
            var scale = Q.From(_vertices[ends.B].Endpoint) - a;
            time = ConvexCertificateFraction.FromCircular(pair.Circular!, a.A, scale.A);
            foreach (var weight in weights)
                if (!weight.TryCompare(ConvexCertificateFraction.From(0), ref work, scratch, out int lo) || lo < 0 ||
                    !weight.TryCompare(ConvexCertificateFraction.From(1), ref work, scratch, out int hi) || hi > 0) return false;
            if (!time.TryCompare(ConvexCertificateFraction.From(0), ref work, scratch, out int lower) || lower < 0 ||
                !time.TryCompare(ConvexCertificateFraction.From(1), ref work, scratch, out int upper) || upper > 0) return false;
        }
        attained = new(this, pair, time, _vertices, weights);
        return true;
    }

    internal static bool TryImpact(in ConvexQueryRequest request, in InstalledSweepLeaf leaf, double from, double to,
        ref ConvexQueryWork work, ConvexQueryScratch scratch, out ConvexImpactCertificate impact)
        => TryImpact(request, leaf, from, to, default, ref work, scratch, out impact);

    internal static bool TryImpact(in ConvexQueryRequest request, in InstalledSweepLeaf leaf, double from, double to,
        in ConvexDistanceCertificate endpoint, ref ConvexQueryWork work, ConvexQueryScratch scratch,
        out ConvexImpactCertificate impact)
    {
        impact = default;
        if (scratch is null || work.Exhausted || work.Descendants == 0 || !ConvexQueryPolicy.Contains(request) ||
            !ConvexIntervalCertificate.ValidInterval(from, to) || !ConvexPlanarSource.TryCreate(leaf, out var original)) return false;
        if (endpoint.IsAlive && !endpoint.IsOwnedBy(scratch)) return false;
        if (!endpoint.IsAlive) scratch.BeginCertificates();
        bool accepted = false;
        try
        {
            for (int i = 0; i < original.VertexCount; i++)
            {
                if (!work.Feature()) return false;
                P p = original.Vertex(i);
                foreach (Q c in new[] { p.X, p.Y, p.Z })
                    if ((Q.From(2048) - c).Sign < 0 || (Q.From(2048) + c).Sign < 0) return false;
            }
            if (!ConvexTemporalSeed.TryCreate(request, original, from, to, endpoint, ref work, scratch, out var simplex)) return false;
            ConvexDistanceCertificate previous = default;
            while (!work.Exhausted)
            {
                if (!ConvexFeatureKernel.TryDistanceCertificate(request, simplex.Source, ConvexCertificateFraction.From(0),
                    ref work, scratch, out var pair, replace: false)) return false;
                if (!pair.TryCompareSquaredMargin(request.Shape.Margin, ref work, scratch, out int sign)) return false;
                if (sign > 0)
                {
                    // Static attainment is the stopping decision, including flat exposed pairs.
                    // A rounded proposal never decides progress or discards an attained event.
                    if (previous.IsAlive && (!pair.TryCompareDistance(previous, ref work, scratch, out int progress) || progress >= 0)) return false;
                    previous = pair;
                    if (!simplex.TryRefine(request, pair, from, to, ref work, scratch, out simplex)) return false;
                    continue;
                }
                if (!simplex.TryAttain(pair, ref work, scratch, out var attained)) return false;
                if (!ConvexImpactCertificate.TryCreate(request, original, attained, from, to, ref work, scratch, out impact)) return false;
                accepted = true;
                return true;
            }
            return false;
        }
        catch (ConvexExactRefusalException) { return work.Refuse(); }
        finally
        {
            if (!accepted) { impact = default; scratch.BeginCertificates(); }
        }
    }

    internal bool TryRefine(in ConvexQueryRequest request, in ConvexDistanceCertificate pair,
        double from, double to, ref ConvexQueryWork work, ConvexQueryScratch scratch, out ConvexTemporalSimplex next)
    {
        next = null!;
        if (!pair.IsOwnedBy(scratch) || !ReferenceEquals(pair.Source.Token, Source.Token)) return false;
        ConvexTemporalVertex support;
        if (pair.IsCircular)
        {
            if (!pair.Circular!.TryTemporalSupport(request, Original, from, to, ref work, scratch, out support)) return false;
        }
        else if (!ConvexQueryOperations.TryTemporalVertex(request, Original, from, to, pair.TowardTarget, ref work, out support)) return false;
        var vertices = scratch.ProposalSimplex;
        Array.Clear(vertices);
        int count = 0;
        if (pair.IsCircular)
        {
            var edge = Source.Edge(pair.LocalFeatureId - Source.FaceCount);
            vertices[count++] = _vertices[edge.A]; vertices[count++] = _vertices[edge.B];
        }
        else
        {
            if (!TryCoordinates(pair.Target, ref work, out var weights, out _)) return false;
            for (int i = 0; i < weights.Length; i++) if (weights[i].Sign > 0) vertices[count++] = _vertices[i];
        }
        for (int i = 0; i < count; i++) if (support.Same(vertices[i])) return false;
        if (count == 4) return false;
        vertices[count++] = support;
        return TryCreate(request, Original, vertices.AsSpan(0, count), out next);
    }
}
