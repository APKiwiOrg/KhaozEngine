using System;

namespace KhaozEngine.Physics.Bepu;

/// <summary>The immutable affine identity of one attained inner-source witness. Root intervals
/// may narrow, but the labelled vertices and exact weight expressions never change. The pair
/// proves primal attainment only. Its inner normal is not an original-member normal.</summary>
internal sealed class ConvexTemporalAttainment
{
    readonly ConvexTemporalVertex[] _vertices;
    readonly ConvexCertificateFraction[] _weights;
    internal ReadOnlySpan<ConvexTemporalVertex> Vertices => _vertices;
    internal ReadOnlySpan<ConvexCertificateFraction> Weights => _weights;
    internal ConvexTemporalSimplex Simplex { get; }
    internal ConvexDistanceCertificate Pair { get; }
    internal ConvexCertificateFraction Time { get; }
    internal bool IsAlive => Pair.IsAlive && Time.IsValid && Simplex.Original.IsValid;

    internal ConvexTemporalAttainment(ConvexTemporalSimplex simplex, ConvexDistanceCertificate pair,
        ConvexCertificateFraction time, ReadOnlySpan<ConvexTemporalVertex> vertices,
        ReadOnlySpan<ConvexCertificateFraction> weights)
    {
        Simplex = simplex; Pair = pair; Time = time;
        _vertices = vertices.ToArray(); _weights = weights.ToArray();
    }
}
