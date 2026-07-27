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
/// implementation must be cheap and must not block.</para>
/// <para><b>The guarantee is scoped to REQUESTS, not results.</b> The gate is consulted only when a chunk is
/// (re-)requested, in the ring-scan step of <c>Update</c> - it is NOT consulted again when that build's result
/// is later applied. A build already in flight when the gate would start refusing the same chunk is not
/// cancelled by that refusal: it still lands once it completes. This is a narrow race under continuous motion
/// (a build racing a residency tile that leaves before the build finishes), not a hole in the common case. The
/// DISCONTINUOUS case - a teleport - is covered separately, by the explicit ordering contract a document-
/// residency layer keeps with its streamer: prime residency for the new focus, then
/// <see cref="TerrainStreamer.UnloadAll"/>, both before the next <c>Update</c>, which discards every in-flight
/// build outright rather than depending on this gate to catch it.</para></summary>
public interface IChunkBuildGate
{
    /// <summary>True when the chunk may be built or rebuilt now. False defers it to a later update.</summary>
    bool CanBuild(ChunkCoord coord);
}
