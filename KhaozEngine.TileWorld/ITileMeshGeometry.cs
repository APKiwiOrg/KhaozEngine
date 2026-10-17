using System.Diagnostics.CodeAnalysis;
using KhaozEngine.Primitives;

namespace KhaozEngine.TileWorld;

/// <summary>The mesh source <see cref="TileWorldClearance.Check"/> reads archetype geometry through, so the tile
/// world stays free of mesh loading. A <c>meshRef</c> is the archetype's own, and the mesh comes back in the
/// archetype's local metres: centred on its footprint, base at y 0, as <see cref="TileObjectPlacement"/> places it.
/// An implementation reports an unreadable or unknown mesh by returning false with a reason rather than throwing.
/// </summary>
public interface ITileMeshGeometry
{
    /// <summary>The mesh behind <paramref name="meshRef"/>, or false with a human-readable reason.</summary>
    bool TryLoad(string meshRef, [NotNullWhen(true)] out ClearanceMesh? mesh, [NotNullWhen(false)] out string? error);
}
