using System.Collections.Generic;
using System.Collections.Immutable;
using System.Numerics;

namespace KhaozEngine.Attachments;

/// <summary>The authored lift data of one skinned attachment mesh.</summary>
public sealed class LiftBasis
{
    internal LiftBasis(List<LiftBasisVertex> vertices) => Vertices = vertices.ToImmutableArray();

    /// <summary>The vertices in flattened mesh, primitive and vertex order.</summary>
    public IReadOnlyList<LiftBasisVertex> Vertices { get; }
}

/// <summary>One vertex's lift data and its location in a material-part upload.</summary>
/// <param name="Direction">The outward unit direction in the body's rest space.</param>
/// <param name="Bearing">The underside's distance from the skin in metres.</param>
/// <param name="MaterialPart">The zero-based source material part in first-use order.</param>
/// <param name="IndexInPart">The zero-based vertex index within that part.</param>
public readonly record struct LiftBasisVertex(Vector3 Direction, float Bearing, int MaterialPart, int IndexInPart);
