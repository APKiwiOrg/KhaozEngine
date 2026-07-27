using System.Collections.Generic;

namespace KhaozEngine.Terrain;

/// <summary>A LIVE source of a placement layer's props, queried at every chunk build instead of bucketed once
/// at sink construction. This is what lets streamed-in content reach the render sink: a frozen
/// <c>PropLayer.Placements</c> list is split into per-chunk buckets when the sink is constructed, so a
/// placement that arrives later would never render no matter how correct the layer above it is.
/// <para><b>Threading.</b> Called on the BUILD thread (the chunk sink's CPU build step), so an implementation
/// publishes an immutable snapshot and reads it ONCE per query, exactly as the sculpt snapshot does. It must
/// not mutate shared state and must not block.</para></summary>
public interface IPlacementSource
{
    /// <summary>Appends the placements whose (X, Z) falls in the HALF-OPEN <paramref name="area"/> into
    /// <paramref name="into"/>, a caller-owned list, so a per-chunk query allocates nothing of its own. Half-open
    /// on both axes matches <see cref="ChunkGrid.AreaOf"/>'s streaming invariant: a placement exactly on the max
    /// edge belongs to the next chunk, which is what makes a partition of chunks reproduce the whole set with
    /// nothing duplicated at a seam.</summary>
    void PlacementsIn(RectArea area, List<PropPlacement> into);
}
