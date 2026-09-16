using System;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// The document-backed <see cref="ITileGroundHeight"/>: the bilinear lattice sample
/// <see cref="TileWorldDocument.HeightAt"/> already computes, which is the same height the terrain mesh, the props
/// and the lights are placed with, raised onto a walkable object top when one covers the point and catalogs were
/// supplied. Wired automatically by <see cref="TilePresenter(TileWorldDocument, TileWorldCatalogs)"/>, so a head
/// that builds its presenter from the world file and its catalogs gets terrain and bridge decks with no call of its
/// own.
/// <para>THIS IS THE ONE PLACE TILE UNITS BECOME WORLD METRES for a height read. The seam takes tile units,
/// <see cref="TileWorldDocument.HeightAt"/> takes world metres, so the two axes go through
/// <see cref="TileWorldSpace.WorldX"/> and <see cref="TileWorldSpace.WorldZ"/> here and nowhere else. Doing it in
/// the presenter instead would put a second copy of the tile-z negation outside <c>TilePresenter.cs</c>, which is
/// the one file in this package allowed to know about it.</para>
/// <para>The document is READ THROUGH on every call rather than sampled once, for the reason
/// <see cref="TileDocumentTargets"/> is: a sculpted region is meant to be visible to the next frame, and a cached
/// lattice would draw bodies on ground the world no longer has. The catalogs are held and read through the same
/// way.</para>
/// <para>The plane is CLAMPED into the document's own range rather than throwing. A presenter easing a body
/// between planes samples the plane above the one it is on, which at the top of the stack is a plane the document
/// does not have, and an exception on a render thread is the wrong answer to a body standing on the roof.</para>
/// </summary>
public sealed class TileDocumentGroundHeight : ITileGroundHeight
{
    readonly TileWorldDocument document;
    readonly TileWorldCatalogs? catalogs;

    /// <summary>Reads TERRAIN heights out of a loaded document and nothing else: the lattice alone, with no object
    /// consulted, so a body on a bridge deck stands on whatever the deck was anchored over. HELD rather than copied,
    /// so an edit rebaked into the world shows up under the next pose drawn over it. Use
    /// <see cref="TileDocumentGroundHeight(TileWorldDocument, TileWorldCatalogs)"/> for a world whose archetypes
    /// carry walk surfaces.</summary>
    /// <param name="document">The world the heights belong to.</param>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
    public TileDocumentGroundHeight(TileWorldDocument document) =>
        this.document = document ?? throw new ArgumentNullException(nameof(document));

    /// <summary>Reads the terrain AND the walkable object tops: the higher of the lattice height and the highest
    /// <see cref="TileObjectArchetype.WalkSurfaces"/> entry covering the point, through
    /// <see cref="TileWalkSurfaces.TryHeightAt"/>. A surface that sits under the terrain loses to it, so a deck
    /// buried by a later sculpt does not pull bodies into the ground. Both arguments are HELD rather than copied.</summary>
    /// <param name="document">The world the heights and the objects belong to.</param>
    /// <param name="catalogs">The archetypes that world's objects reference, which carry the walk surfaces.</param>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> or <paramref name="catalogs"/> is
    /// null.</exception>
    public TileDocumentGroundHeight(TileWorldDocument document, TileWorldCatalogs catalogs)
    {
        this.document = document ?? throw new ArgumentNullException(nameof(document));
        this.catalogs = catalogs ?? throw new ArgumentNullException(nameof(catalogs));
    }

    /// <inheritdoc/>
    public float HeightAt(float tileX, float tileZ, int plane)
    {
        float worldX = TileWorldSpace.WorldX(tileX, document.TileSize);
        float worldZ = TileWorldSpace.WorldZ(tileZ, document.TileSize);
        int clamped = Math.Clamp(plane, 0, document.PlaneCount - 1);
        float terrain = document.HeightAt(worldX, worldZ, clamped);
        return catalogs is not null
            && TileWalkSurfaces.TryHeightAt(document, catalogs, worldX, worldZ, clamped, out float surface)
            && surface > terrain
            ? surface
            : terrain;
    }
}
