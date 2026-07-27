namespace KhaozEngine.Terrain;

/// <summary>Gates which chunks a <see cref="TerrainStreamer"/> may build. Returning false DEFERS the chunk: it
/// is not requested, not marked loaded, and it is reconsidered on the next <see cref="TerrainStreamer.Update"/>.
/// A null gate (<see cref="TerrainStreamer.BuildGate"/>'s default) means every chunk in the ring is eligible,
/// which is the pre-gate behaviour exactly.
/// <para>The case this exists for: a streamer composed with a document-residency layer whose tiles load
/// asynchronously. Continuous motion can outrun async residency (a vehicle, a slow disk, a fat tile), and no
/// ordering rule fixes that, so a chunk whose authored data has not arrived would otherwise build against the
/// bare analytic field and hand the player a collider for terrain that is about to change shape. A deferred
/// chunk is not a failure mode, it is the streamer's ordinary "not yet": it arrives a few frames later when its
/// data does.</para>
/// <para>Called on the frame thread from inside <c>Update</c>, once per candidate chunk per update, so an
/// implementation must be cheap and must not block.</para></summary>
public interface IChunkBuildGate
{
    /// <summary>True when the chunk may be built or rebuilt now. False defers it to a later update.</summary>
    bool CanBuild(ChunkCoord coord);
}
