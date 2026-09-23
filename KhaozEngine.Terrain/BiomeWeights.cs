using System;

namespace KhaozEngine.Terrain
{
    /// <summary>How much of each <see cref="BiomeId"/> applies at one world point: the normalized band weights
    /// <see cref="TerrainField.SampleBiomeWeights"/> reads off the same smoothstep blend that shapes the height. The
    /// shares change continuously across a band boundary, where <see cref="TerrainField.SampleBiome"/> switches at a
    /// line. A field sample has non-negative shares summing to 1, and its <see cref="Dominant"/> is exactly what
    /// <c>SampleBiome</c> returns at the same point. <c>default</c> has no shares at all. Render-free value type.</summary>
    public readonly struct BiomeWeights
    {
        /// <summary>One slot per <see cref="BiomeId"/> value, indexed by the enum's numeric value. A test pins this
        /// to the enum, so adding a biome fails loudly until this type carries it.</summary>
        internal const int Count = 6;

        readonly float _meadow, _forest, _marsh, _mountains, _desert, _snow;

        /// <summary>Takes <paramref name="shares"/> indexed by <see cref="BiomeId"/> numeric value (length
        /// <see cref="Count"/>) as they are. The caller normalizes.</summary>
        internal BiomeWeights(ReadOnlySpan<float> shares, BiomeId dominant)
        {
            _meadow = shares[(int)BiomeId.Meadow];
            _forest = shares[(int)BiomeId.Forest];
            _marsh = shares[(int)BiomeId.Marsh];
            _mountains = shares[(int)BiomeId.Mountains];
            _desert = shares[(int)BiomeId.Desert];
            _snow = shares[(int)BiomeId.Snow];
            Dominant = dominant;
        }

        /// <summary>The biome of the strongest band at the point, ties going to the earlier band. Identical to
        /// <see cref="TerrainField.SampleBiome"/> at the same point.</summary>
        public BiomeId Dominant { get; }

        /// <summary>This biome's share at the point, 0 to 1. An id outside the enum has no share.</summary>
        public float this[BiomeId biome] => biome switch
        {
            BiomeId.Meadow => _meadow,
            BiomeId.Forest => _forest,
            BiomeId.Marsh => _marsh,
            BiomeId.Mountains => _mountains,
            BiomeId.Desert => _desert,
            BiomeId.Snow => _snow,
            _ => 0f,
        };

        /// <summary>All of one biome, which is what a point well inside a single band samples as.</summary>
        public static BiomeWeights Single(BiomeId biome)
        {
            Span<float> shares = stackalloc float[Count];
            if ((uint)biome < Count) shares[(int)biome] = 1f;
            return new BiomeWeights(shares, biome);
        }
    }
}
