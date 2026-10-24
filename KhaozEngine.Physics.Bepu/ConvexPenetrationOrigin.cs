using P = KhaozEngine.Physics.Bepu.ConvexExactPoint;

namespace KhaozEngine.Physics.Bepu;

/// <summary>A strict inner-CSO inball proves original-member overlap. It carries no
/// invented distance pair or common point, and retains the original admission's lifetime.</summary>
internal readonly struct ConvexPenetrationOrigin(ConvexQueryRequest request, ConvexPlanarSource source,
    ConvexCertificateFraction fraction, ConvexQueryScratch scratch, GeometryInterval inball)
{
    readonly int _generation = scratch.CertificateGeneration;
    internal ConvexQueryRequest Request => request;
    internal ConvexPlanarSource Source => source;
    internal ConvexCertificateFraction Fraction => fraction;
    internal P Feet => P.From(request.Pose.Position) + P.From(request.Displacement) * fraction.Value;
    internal ConvexInitialState State => ConvexInitialState.Overlapping;
    internal bool IsAlive => scratch is not null && _generation == scratch.CertificateGeneration &&
        source.IsOriginal && source.IsValid && inball.IsResolved && inball.Lower > 0;
    internal bool IsOwnedBy(ConvexQueryScratch owner) => IsAlive && ReferenceEquals(owner, scratch);
}
