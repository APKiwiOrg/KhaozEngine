using System;
using System.Collections.Generic;

namespace KhaozEngine.Terrain
{
    /// <summary>One terrain LOD tier: a grid resolution (segments per chunk edge) and the metre distance below which
    /// a chunk uses this tier. Tiers get coarser (lower resolution) with distance, so a config's tiers descend in
    /// resolution and ascend in <see cref="MaxDistance"/>. The coarsest (last) tier covers everything beyond the
    /// previous tier's threshold, so its <see cref="MaxDistance"/> is <see cref="float.PositiveInfinity"/>.</summary>
    public readonly struct TerrainLodTier
    {
        /// <summary>Grid resolution: segments per chunk edge (the meshed grid is (Resolution+1)^2 vertices). >= 1.</summary>
        public int Resolution { get; }

        /// <summary>A chunk whose camera distance is strictly less than this (metres) picks this tier. The coarsest
        /// tier uses <see cref="float.PositiveInfinity"/> so it catches everything past the previous threshold.</summary>
        public float MaxDistance { get; }

        /// <summary>Build a tier. <paramref name="resolution"/> must be at least 1; <paramref name="maxDistance"/>
        /// must be positive (finite for every tier but the coarsest, which uses positive infinity).</summary>
        public TerrainLodTier(int resolution, float maxDistance)
        {
            if (resolution < 1)
                throw new ArgumentOutOfRangeException(nameof(resolution), resolution, "Tier resolution must be at least 1 segment per edge.");
            if (float.IsNaN(maxDistance) || maxDistance <= 0f)
                throw new ArgumentOutOfRangeException(nameof(maxDistance), maxDistance, "Tier max distance must be positive (use float.PositiveInfinity for the coarsest tier).");
            Resolution = resolution;
            MaxDistance = maxDistance;
        }
    }

    /// <summary>Data-driven distance-to-LOD mapping for chunked terrain, the configurable form of what
    /// <see cref="TerrainLod"/> used to hardcode. An ordered list of <see cref="TerrainLodTier"/>s, validated so
    /// resolutions strictly descend and max distances strictly ascend with distance (near = dense, far = coarse),
    /// with the coarsest tier's <see cref="TerrainLodTier.MaxDistance"/> at positive infinity. <see cref="PickLod(float)"/>
    /// maps a camera distance to a tier index; <see cref="ResolutionFor"/> maps that tier to its grid resolution.
    /// <para>The same config must be wired to both the <see cref="TerrainStreamer"/> (via
    /// <see cref="StreamerConfig.LodConfig"/>, which picks the tier per chunk) and the <c>Scene3DChunkSink</c>
    /// (which meshes at that tier's resolution), so a tier index means the same resolution on both sides. Both
    /// default to <see cref="Default"/>, so the default wiring aligns with no work. <see cref="Default"/> reproduces
    /// the legacy 64/32/16 tiers at 80 m/200 m byte-for-byte and extends them with coarser 8- and 4-segment far
    /// tiers so a distant chunk costs a few hundred triangles instead of the mid tier's few thousand.</para></summary>
    public sealed class TerrainLodConfig
    {
        readonly TerrainLodTier[] _tiers;

        /// <summary>The ordered tiers, tier 0 (densest, nearest) first.</summary>
        public IReadOnlyList<TerrainLodTier> Tiers => _tiers;

        /// <summary>How many tiers this config defines (>= 1).</summary>
        public int TierCount => _tiers.Length;

        /// <summary>Build a config from ordered tiers (tier 0 first). Validates at least one tier, strictly
        /// descending resolutions, strictly ascending max distances, and a coarsest tier at positive infinity.</summary>
        public TerrainLodConfig(params TerrainLodTier[] tiers)
        {
            if (tiers is null) throw new ArgumentNullException(nameof(tiers));
            if (tiers.Length == 0)
                throw new ArgumentException("A LOD config needs at least one tier.", nameof(tiers));
            for (int i = 1; i < tiers.Length; i++)
            {
                if (tiers[i].Resolution >= tiers[i - 1].Resolution)
                    throw new ArgumentException(
                        $"Tier {i} resolution {tiers[i].Resolution} must be strictly less than tier {i - 1}'s {tiers[i - 1].Resolution} (tiers get coarser with distance).",
                        nameof(tiers));
                if (tiers[i].MaxDistance <= tiers[i - 1].MaxDistance)
                    throw new ArgumentException(
                        $"Tier {i} max distance {tiers[i].MaxDistance} must be strictly greater than tier {i - 1}'s {tiers[i - 1].MaxDistance} (tiers reach further with distance).",
                        nameof(tiers));
            }
            if (!float.IsPositiveInfinity(tiers[^1].MaxDistance))
                throw new ArgumentException(
                    "The coarsest (last) tier must have MaxDistance = float.PositiveInfinity so it covers everything beyond the previous threshold.",
                    nameof(tiers));
            _tiers = (TerrainLodTier[])tiers.Clone();
        }

        /// <summary>The tier index for a camera distance: the first tier whose <see cref="TerrainLodTier.MaxDistance"/>
        /// the distance is strictly under, so tier 0 within the nearest threshold and the coarsest tier beyond the
        /// last finite one. Monotone non-decreasing in distance.</summary>
        public int PickLod(float distance)
        {
            for (int i = 0; i < _tiers.Length; i++)
                if (distance < _tiers[i].MaxDistance) return i;
            return _tiers.Length - 1;
        }

        /// <summary>Default dead zone for the hysteresis <see cref="PickLod(float, int, float)"/>, in metres. A player
        /// must cover 20 m (twice the margin) to flip a chunk's tier back and forth, about 3 s at a brisk 6 m/s run,
        /// and it stays small against this table's 80 m and 200 m boundaries so a chunk is never visibly stuck on a
        /// coarse tier.</summary>
        public const float DefaultHysteresis = 10f;

        /// <summary>The tier for a camera distance with a dead zone around every boundary: a chunk already built at
        /// <paramref name="currentLod"/> keeps that tier until the distance clears the boundary it would cross by
        /// <paramref name="hysteresis"/> metres. Without it a chunk parked near a boundary re-tiers on every small
        /// move, and a re-tier frees a live GPU mesh, so a walking player pays a mesh rebuild per step.
        /// <para>A <paramref name="currentLod"/> outside the tier range (use -1 for "not built yet") or a
        /// <paramref name="hysteresis"/> that is not positive returns <see cref="PickLod(float)"/> unchanged, so a
        /// first load and a static viewer see exactly the stateless tiers. The damping is on the CHANGE only: once
        /// the margin is cleared the tier tracks the distance in full, including a multi-tier jump.</para></summary>
        public int PickLod(float distance, int currentLod, float hysteresis)
        {
            int next = PickLod(distance);
            if (currentLod < 0 || currentLod >= _tiers.Length || !(hysteresis > 0f) || next == currentLod) return next;
            // Moving away: the boundary being crossed is the current tier's own reach. Moving closer: it is the
            // previous tier's, which is the lower edge of the band the chunk currently sits in.
            return next > currentLod
                ? (distance >= _tiers[currentLod].MaxDistance + hysteresis ? next : currentLod)
                : (distance <= _tiers[currentLod - 1].MaxDistance - hysteresis ? next : currentLod);
        }

        /// <summary>Grid resolution (segments per chunk edge) for a tier index, clamped to the valid range.</summary>
        public int ResolutionFor(int lod) => _tiers[Math.Clamp(lod, 0, _tiers.Length - 1)].Resolution;

        /// <summary>The shallowest skirt any tier gets, in metres. This is the flat depth every chunk used before the
        /// skirt became tier-aware, kept as a FLOOR so no tier ever gets a shallower skirt than the near ring
        /// shipped with, however dense a config's cells are.</summary>
        public const float MinSkirtDepth = 0.3f;

        /// <summary>How deep a skirt hangs below the chunk edge, as a fraction of the coarsest cell that can meet
        /// that edge. Half a cell is the height a cell climbing at 45 degrees straddles either side of its own chord,
        /// and the chord is exactly what a coarse chunk renders between its samples, so this is the vertical gap a
        /// seam can open on terrain that steep. Measured against the engine's own presets (Clearing's mountain band
        /// and BoundedClearing, every tier pair of <see cref="Default"/>, 8 chunk columns each) the worst real gap
        /// came to 0.47 of the coarse cell, so 0.5 covers them with a little room for a rougher field.</summary>
        public const float SkirtCellFraction = 0.5f;

        /// <summary>Skirt depth in metres for a chunk meshed at <paramref name="lod"/> on a
        /// <paramref name="chunkSize"/> m grid: <paramref name="cellFraction"/> of the COARSEST cell that can meet
        /// this chunk's edge, floored at <see cref="MinSkirtDepth"/>.
        /// <para>The gap a skirt hides is the NEIGHBOUR's, not this chunk's. Two chunks that share an edge sample the
        /// same boundary line at different spacings, and between the coarse side's samples its surface is a straight
        /// chord while the fine side follows the field, so the vertical slit between them is bounded by how far the
        /// field departs from that chord across ONE COARSE CELL. That is why a flat depth cracks at range: it is the
        /// coarse cell that grows with distance, by 16x from <see cref="Default"/>'s near tier to its far one.</para>
        /// <para>The coarsest neighbour is derived from the table itself rather than assumed to be one tier out: LOD
        /// is picked per chunk from the metre distance to its CENTER, edge-sharing centres are exactly
        /// <paramref name="chunkSize"/> apart, and hysteresis can hold a chunk on its old tier for another
        /// <see cref="DefaultHysteresis"/> metres, so the coarsest tier reachable from this one is what
        /// <see cref="PickLod(float)"/> returns that far past this tier's own reach. On <see cref="Default"/> that is
        /// simply the next tier, but a table with thresholds packed closer than a chunk is wide can skip one, and
        /// then the deeper skirt is exactly what the seam needs.</para></summary>
        public float SkirtDepthFor(int lod, float chunkSize, float cellFraction = SkirtCellFraction)
        {
            int tier = Math.Clamp(lod, 0, _tiers.Length - 1);
            float reach = _tiers[tier].MaxDistance;
            int coarsest = float.IsPositiveInfinity(reach)
                ? _tiers.Length - 1
                : PickLod(reach + chunkSize + DefaultHysteresis);
            return MathF.Max(MinSkirtDepth, cellFraction * (chunkSize / _tiers[coarsest].Resolution));
        }

        /// <summary>The default tiers: the legacy 64/32/16 at 80 m/200 m (byte-identical meshes for existing callers),
        /// extended with an 8-segment tier out to 500 m, another to 1000 m, and a 4-segment tier beyond. The near
        /// three tiers reproduce today's behaviour exactly; the far tiers only engage past 200 m, where nothing
        /// loaded before a far radius / decor ring is configured. The byte-identical claim is about the RESOLUTIONS,
        /// which have not moved. What did move is the skirt depth a streamed chunk is meshed with, once it stopped
        /// being flat (see <see cref="SkirtDepthFor"/>).</summary>
        public static TerrainLodConfig Default { get; } = new(
            new TerrainLodTier(64, 80f),
            new TerrainLodTier(32, 200f),
            new TerrainLodTier(16, 500f),
            new TerrainLodTier(8, 1000f),
            new TerrainLodTier(4, float.PositiveInfinity));
    }
}
