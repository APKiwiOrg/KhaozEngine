using System;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// The document-backed <see cref="ITileGroundHeight"/>: the bilinear lattice sample
/// <see cref="TileWorldDocument.HeightAt"/> already computes, which is the same height the terrain mesh, the props
/// and the lights are placed with. Wired automatically by <see cref="TilePresenter(TileWorldDocument)"/>, so a head
/// that builds its presenter from the world file gets terrain with no call of its own.
/// <para>THIS IS THE ONE PLACE TILE UNITS BECOME WORLD METRES for a height read. The seam takes tile units,
/// <see cref="TileWorldDocument.HeightAt"/> takes world metres, so the two axes go through
/// <see cref="TileWorldSpace.WorldX"/> and <see cref="TileWorldSpace.WorldZ"/> here and nowhere else. Doing it in
/// the presenter instead would put a second copy of the tile-z negation outside <c>TilePresenter.cs</c>, which is
/// the one file in this package allowed to know about it.</para>
/// <para>The document is READ THROUGH on every call rather than sampled once, for the reason
/// <see cref="TileDocumentTargets"/> is: a sculpted region is meant to be visible to the next frame, and a cached
/// lattice would draw bodies on ground the world no longer has.</para>
/// <para>The plane is CLAMPED into the document's own range rather than throwing. A presenter easing a body
/// between planes samples the plane above the one it is on, which at the top of the stack is a plane the document
/// does not have, and an exception on a render thread is the wrong answer to a body standing on the roof.</para>
/// </summary>
public sealed class TileDocumentGroundHeight : ITileGroundHeight
{
    readonly TileWorldDocument document;

    /// <summary>Reads heights out of a loaded document. HELD rather than copied, so an edit rebaked into the world
    /// shows up under the next pose drawn over it.</summary>
    /// <param name="document">The world the heights belong to.</param>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
    public TileDocumentGroundHeight(TileWorldDocument document) =>
        this.document = document ?? throw new ArgumentNullException(nameof(document));

    /// <inheritdoc/>
    public float HeightAt(float tileX, float tileZ, int plane) =>
        document.HeightAt(TileWorldSpace.WorldX(tileX, document.TileSize),
            TileWorldSpace.WorldZ(tileZ, document.TileSize),
            Math.Clamp(plane, 0, document.PlaneCount - 1));
}
