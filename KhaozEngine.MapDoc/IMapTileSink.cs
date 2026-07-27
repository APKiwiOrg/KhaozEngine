using KhaozEngine.Terrain;

namespace KhaozEngine.MapDoc;

/// <summary>Residency notifications: the engine tells you a document tile arrived, changed ring, or left. What
/// you build from it is yours.
/// <para><b>The physics seam, stated plainly: the engine notifies, the consumer populates.</b> Nothing in the
/// residency layer registers, owns, or frees a physics body. Per-tile add and remove of static bodies from
/// these callbacks is the INTENDED use, and nothing here forbids, batches, defers or wraps it.</para>
/// <para><b>Threading.</b> Every method fires on the thread that called
/// <see cref="MapTileResidency.Update(System.Numerics.Vector3)"/> (or <c>PrimeAround</c> / <c>UnloadAll</c> /
/// <c>Invalidate</c>), before that call returns, so a consumer registers and frees physics bodies with no lock.
/// The file read and parse that produced <c>content</c> ran on a worker thread, but the handoff to you does
/// not.</para></summary>
public interface IMapTileSink
{
    /// <summary>The tile entered residency, at <paramref name="ring"/>. Fires exactly once per arrival. This is
    /// where a consumer walks <c>content.Placements</c> to add static bodies, folds
    /// <c>content.SculptTiles</c> into the terrain field, and invalidates the chunks over the tile's rect.</summary>
    void TileLoaded(MapTileCoord coord, MapTileContent content, ChunkRing ring);

    /// <summary>The tile stayed resident but changed ring (<see cref="ChunkRing.Gameplay"/> to
    /// <see cref="ChunkRing.Decor"/> or back). This is where a consumer sheds colliders for a far tile without
    /// dropping its data: the content is still loaded and <c>TryGetContent</c> still returns it.</summary>
    void TileRingChanged(MapTileCoord coord, MapTileContent content, ChunkRing ring);

    /// <summary>The tile left residency. Fires exactly once per departure. This is where a consumer removes the
    /// bodies it added and drops the tile's sculpt from the field.</summary>
    void TileUnloaded(MapTileCoord coord);
}
