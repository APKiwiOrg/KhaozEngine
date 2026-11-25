using System;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// The document-backed <see cref="ITileGroundHeight"/> samples the terrain triangle used by the full-detail mesh
/// and colliders through <see cref="TileGroundHeight.TrySample"/>. Where the document draws no triangle, it reads
/// the bilinear <see cref="TileWorldDocument.HeightAt"/>. If catalogs were supplied, a higher walkable object top
/// covering the point takes precedence. The document-backed <see cref="TilePresenter"/> constructors wire this
/// source automatically.
/// <para>The seam takes tile units and the document queries take world metres. This adapter converts the axes
/// through <see cref="TileWorldSpace.WorldX"/> and <see cref="TileWorldSpace.WorldZ"/>.</para>
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

    /// <summary>Reads terrain heights from the live document, so a body on a bridge draws on the terrain beneath
    /// it and edits show under the next pose. Use
    /// <see cref="TileDocumentGroundHeight(TileWorldDocument, TileWorldCatalogs)"/> for a world whose archetypes
    /// carry walk surfaces.</summary>
    /// <param name="document">The world the heights belong to.</param>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
    public TileDocumentGroundHeight(TileWorldDocument document) =>
        this.document = document ?? throw new ArgumentNullException(nameof(document));

    /// <summary>Reads the higher of the terrain height and the highest covering
    /// <see cref="TileObjectArchetype.WalkSurfaces"/> entry through <see cref="TileWalkSurfaces.TryHeightAt"/>.
    /// Both arguments are held live so edits show under the next pose.</summary>
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
        if (!TileGroundHeight.TrySample(document, worldX, worldZ, clamped, out float terrain))
            terrain = document.HeightAt(worldX, worldZ, clamped);
        return catalogs is not null
            && TileWalkSurfaces.TryHeightAt(document, catalogs, worldX, worldZ, clamped, out float surface)
            && surface > terrain
            ? surface
            : terrain;
    }
}
