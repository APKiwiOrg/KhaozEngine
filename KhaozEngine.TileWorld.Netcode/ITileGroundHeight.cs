namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// The GROUND a pose stands on: how high the terrain is under a planar point. A seam rather than a document
/// reference, so a test can hand <see cref="TilePresenter"/> a synthetic slope with no world file and a future head
/// can substitute a streamed source without the presenter learning what streaming is.
/// <para>TILE UNITS IN, METRES OUT, and that split is deliberate. Everything the presenter holds on the planar axes
/// is in tile units, because that is the space the lattice, the footprint centre and the glide all live in, while
/// the answer is a world height because it goes straight into a <see cref="System.Numerics.Vector3"/> the head
/// draws at. The one conversion between the two spaces belongs to the implementation, and
/// <see cref="TileDocumentGroundHeight"/> is where it lives for a document-backed world.</para>
/// <para>PURE, and called once per drawn body per frame. No allocation, no state, and the same point asked twice
/// answers twice the same, because two callers drawing the same body have to agree about where its feet are.</para>
/// </summary>
public interface ITileGroundHeight
{
    /// <summary>
    /// The ground height in METRES at a planar point given in TILE units on the lattice (x, z). The point is the
    /// one the body is DRAWN at, tile centres included, so a caller passes 4.5 for the middle of tile 4 rather
    /// than 4.
    /// </summary>
    /// <param name="tileX">Where to sample, in tile units east.</param>
    /// <param name="tileZ">Where to sample, in tile units north. Tile north, not render z.</param>
    /// <param name="plane">Which plane, as an index. An implementation clamps rather than throws for a plane its
    /// world does not have, because a presenter easing a body between two planes samples the one above the top.</param>
    /// <returns>The height in world metres, which is what a pose's Y carries.</returns>
    float HeightAt(float tileX, float tileZ, int plane);
}
