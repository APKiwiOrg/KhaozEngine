using System;
using System.Collections.Generic;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapDoc;

/// <summary>Tuning for <see cref="MapTileResidency"/>. Radii are in DOCUMENT TILE units at CHEBYSHEV tile
/// distance (a square ring, <c>max(|dx|, |dz|)</c>), which is NOT <see cref="StreamerConfig"/>'s Euclidean
/// metric, and the difference is deliberate rather than an oversight.
/// <para><b>Why Chebyshev.</b> The focus can sit anywhere in its own tile, including hard against a corner, so
/// the only guarantee worth stating is the distance to the nearest NON-resident tile from the worst position.
/// A Euclidean ring gives 0 tiles of guaranteed coverage at radius 1 (the diagonal neighbour is excluded and
/// the focus can be arbitrarily close to it), 1 at radius 2, and an awkward 2.83 at radius 4, where the binding
/// tile is the diagonal (3, 3) rather than the axial (5, 0). A Chebyshev ring gives exactly
/// <c>LoadRadius * tileSize</c> for every radius, with no special cases, which is what makes
/// <see cref="ValidateAgainst"/> a single line of arithmetic that is actually true. A chunk is a render
/// primitive where a round ring saves builds in the corners, and a document tile is a data-availability unit
/// where a square ring is the thing that can be reasoned about.</para>
/// <para>A distinct type from <see cref="StreamerConfig"/> on purpose: its <c>ChunkSize</c> and <c>LodConfig</c>
/// are meaningless here, and one shared type would let a chunk config be passed to document residency by
/// accident, at which point the metric difference above becomes a silent hole in the world.</para>
/// <para><see cref="DecorRadius"/> extends the loaded ring past <see cref="LoadRadius"/> with tiles marked
/// <see cref="ChunkRing.Decor"/>: fully loaded data a consumer renders but does not simulate.
/// <see cref="UnloadRadius"/> is the hysteresis boundary and must exceed <see cref="OuterRadius"/>.</para></summary>
public readonly record struct MapResidencyConfig(
    int LoadRadius, int UnloadRadius, int MaxLoadsPerUpdate, int DecorRadius = 0, bool Async = true)
{
    /// <summary>LoadRadius 2, UnloadRadius 3, 2 applies per update, no decor ring. At the 512 m default tile
    /// that is a 5x5 (2,560 m) gameplay square around the focus, 1,024 m of guaranteed coverage in every
    /// direction before the sculpt inset, and a 1-tile (512 m) hysteresis band.
    /// <para>2 and not 1, because a default that only works for one streamer config is not a default.
    /// LoadRadius 1 does pass <see cref="ValidateAgainst"/> at <see cref="StreamerConfig.Default"/>, by 15 m out
    /// of a 512 m tile, and fails the moment a game turns on a decor ring: at DecorRadius 8 / UnloadRadius 10
    /// the worst-case chunk reach is 684 m, which LoadRadius 1 cannot cover and LoadRadius 2 covers with 276 m
    /// to spare. One tile of hysteresis is 512 m of focus travel, which absorbs boundary oscillation completely
    /// and caps the worst-case resident set at (2*3+1)^2 = 49 tiles rather than the 81 a 2-tile band allows.
    /// MaxLoadsPerUpdate 2 fills the 5-tile column a boundary crossing brings into range in 3 updates, against
    /// the 85 s it takes to cross a 512 m tile at 6 m/s.</para></summary>
    public static MapResidencyConfig Default => new(LoadRadius: 2, UnloadRadius: 3, MaxLoadsPerUpdate: 2);

    /// <summary>The outer load extent in tile units: the larger of <see cref="LoadRadius"/> and
    /// <see cref="DecorRadius"/>. Tiles load out to here, and those past <see cref="LoadRadius"/> are
    /// <see cref="ChunkRing.Decor"/>.</summary>
    public int OuterRadius => DecorRadius > LoadRadius ? DecorRadius : LoadRadius;

    /// <summary>This config with async tile reads turned off: every load is read inline on the calling thread
    /// during <see cref="MapTileResidency.Update(System.Numerics.Vector3)"/>, still capped at
    /// <see cref="MaxLoadsPerUpdate"/> per update. For editors, tools and tests that want deterministic
    /// loads.</summary>
    public MapResidencyConfig Synchronous() => this with { Async = false };

    /// <summary>Errors (an empty list means fine) when this config cannot cover a terrain streamer's chunk ring,
    /// so that a chunk can never build or REBUILD against a non-resident document tile. Checked by the consumer
    /// at wiring time, against the WIDEST render-distance profile rather than the active one: the profile is a
    /// runtime setting, and a config that only validates on Low is a hole in the world on Ultra.
    /// <para>Two rules, both derived rather than asserted. <c>maxChunkReach(R)</c> is the worst-case world
    /// distance a loaded chunk reaches, <c>ChunkSize * max{ sqrt((|dx|+1)^2 + (|dz|+1)^2) }</c> over the integer
    /// disk <c>dx^2 + dz^2 &lt;= R^2</c>. That maximum is NOT the axial (R, 0) case: at R = 6 the worst offset is
    /// (5, 3), giving 7.211 chunks against the 7.071 an axial-only reading gives.</para>
    /// <list type="bullet">
    /// <item><b>Data rule.</b> <c>OuterRadius * tileSize - sculptSpan &gt;= maxChunkReach(streamer.UnloadRadius)</c>.
    /// UnloadRadius and not OuterRadius, because chunks persist out to the streamer's unload radius and
    /// <c>TerrainStreamer.Invalidate</c> rebuilds every LOADED chunk a rect touches, which the sculpt handoff
    /// calls on every tile arrival. So a chunk anywhere out to UnloadRadius can be rebuilt at any moment and must
    /// find its document data resident when it is.</item>
    /// <item><b>Collider rule.</b> <c>LoadRadius * tileSize - sculptSpan &gt;= maxChunkReach(streamer.LoadRadius)</c>.
    /// Every gameplay chunk (the ones that register scatter and colliders) sits over Gameplay document tiles, so
    /// a consumer that sheds colliders on a Decor tile never sheds them under a live gameplay chunk.</item>
    /// </list>
    /// <para><c>sculptSpan</c> is <c>TerrainSculpt.TileSize * sculptCellSize</c> and is subtracted because a
    /// document tile's sculpt coverage is inset: its low-X and low-Z edges are covered by sculpt owned by the
    /// neighbour on that side, so a resident tile whose neighbour is absent carries no authored deltas over its
    /// first <c>sculptSpan</c> metres. Hysteresis does not pay for this - hysteresis is an unload-side allowance
    /// and the shortfall is on the load side.</para></summary>
    /// <param name="streamer">The chunk streamer config this residency has to cover.</param>
    /// <param name="tileSize">The document's tile edge in metres (<c>MapDocument.TileSize</c>).</param>
    /// <param name="sculptCellSize">The document's sculpt cell size in metres
    /// (<c>MapTerrainOverrides.CellSize</c>).</param>
    public IReadOnlyList<string> ValidateAgainst(StreamerConfig streamer, float tileSize, float sculptCellSize)
    {
        var errors = new List<string>();
        if (!(tileSize > 0f) || float.IsInfinity(tileSize))
        {
            errors.Add($"tileSize ({tileSize}) must be positive and finite.");
            return errors;
        }
        if (!(sculptCellSize > 0f) || float.IsInfinity(sculptCellSize))
        {
            errors.Add($"sculptCellSize ({sculptCellSize}) must be positive and finite.");
            return errors;
        }
        if (LoadRadius < 0) errors.Add($"LoadRadius ({LoadRadius}) must not be negative.");
        if (MaxLoadsPerUpdate <= 0) errors.Add($"MaxLoadsPerUpdate ({MaxLoadsPerUpdate}) must be positive.");
        if (UnloadRadius <= OuterRadius)
            errors.Add($"UnloadRadius ({UnloadRadius}) must exceed the outer load radius ({OuterRadius}) so the hysteresis band stops churn.");

        float sculptSpan = TerrainSculpt.TileSize * sculptCellSize;
        float dataCover = OuterRadius * tileSize - sculptSpan;
        float dataNeed = MaxChunkReach(streamer.UnloadRadius, streamer.ChunkSize);
        if (dataCover < dataNeed)
            errors.Add($"data rule: OuterRadius {OuterRadius} covers {dataCover:0.##} m after the {sculptSpan:0.##} m sculpt inset, " +
                       $"but a chunk out at the streamer's UnloadRadius {streamer.UnloadRadius} reaches {dataNeed:0.##} m. " +
                       "A chunk would build or rebuild against a non-resident document tile.");

        float colliderCover = LoadRadius * tileSize - sculptSpan;
        float colliderNeed = MaxChunkReach(streamer.LoadRadius, streamer.ChunkSize);
        if (colliderCover < colliderNeed)
            errors.Add($"collider rule: LoadRadius {LoadRadius} covers {colliderCover:0.##} m after the {sculptSpan:0.##} m sculpt inset, " +
                       $"but a gameplay chunk out at the streamer's LoadRadius {streamer.LoadRadius} reaches {colliderNeed:0.##} m. " +
                       "A gameplay chunk would sit over a Decor document tile whose colliders a consumer has shed.");

        return errors;
    }

    /// <summary>The worst-case world distance any chunk in a Euclidean ring of radius <paramref name="radius"/>
    /// reaches, measured from a focus sitting at the far corner of its own chunk. Brute-forced over the integer
    /// disk rather than assumed axial, because the maximum lands on an off-axis offset for most radii.</summary>
    static float MaxChunkReach(int radius, float chunkSize)
    {
        if (radius < 0) return 0f;
        long best = 0;
        long rSq = (long)radius * radius;
        for (int dz = -radius; dz <= radius; dz++)
        for (int dx = -radius; dx <= radius; dx++)
        {
            if ((long)dx * dx + (long)dz * dz > rSq) continue;
            long ax = Math.Abs(dx) + 1L, az = Math.Abs(dz) + 1L;
            long reachSq = ax * ax + az * az;
            if (reachSq > best) best = reachSq;
        }
        return chunkSize * MathF.Sqrt(best);
    }
}
