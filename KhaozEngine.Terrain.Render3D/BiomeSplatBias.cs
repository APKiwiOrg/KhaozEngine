namespace KhaozEngine.Terrain
{
    /// <summary>The per-biome tilt on the engine's default splat mix: the fraction of a vertex's GRASS weight that
    /// moves to each other channel. Grass is what the physical rules in <see cref="TerrainSplatWeights"/> leave over
    /// once steepness, shore and snow line have taken their share, so a biome can only recolour open ground. It never
    /// takes weight from rock on a cliff, sand at the water or snow above the snow line. Fractions are non-negative
    /// and sum to at most 1.
    /// <list type="table">
    /// <item><term>Meadow</term><description>None. The default biome, so an all-Meadow world bakes exactly the
    /// pre-biome mix.</description></item>
    /// <item><term>Forest</term><description>0.30 to dirt. Leaf litter and bare humus under a canopy that shades the
    /// grass out, so the floor reads darker and browner than a meadow while staying green.</description></item>
    /// <item><term>Marsh</term><description>0.50 to dirt. Waterlogged mud between grass tussocks, an even mix.</description></item>
    /// <item><term>Mountains</term><description>0.30 to rock, 0.15 to dirt. Thin soil over bedrock, so a ledge or
    /// valley floor reads stony instead of lowland lawn. Slopes already turn to rock through the steepness rule.</description></item>
    /// <item><term>Desert</term><description>0.80 to sand, 0.15 to dirt. Open ground is sand with hardpan patches
    /// and a trace of scrub.</description></item>
    /// <item><term>Snow</term><description>0.85 to snow. Snow cover below the snow line with a little tundra showing
    /// through.</description></item>
    /// </list></summary>
    internal readonly struct BiomeSplatBias
    {
        public readonly float Dirt, Rock, Sand, Snow;

        BiomeSplatBias(float dirt, float rock, float sand, float snow)
        {
            Dirt = dirt; Rock = rock; Sand = sand; Snow = snow;
        }

        public bool IsZero => Dirt == 0f && Rock == 0f && Sand == 0f && Snow == 0f;

        public float Total => Dirt + Rock + Sand + Snow;

        static readonly BiomeSplatBias ForestBias = new(dirt: 0.30f, rock: 0f, sand: 0f, snow: 0f);
        static readonly BiomeSplatBias MarshBias = new(dirt: 0.50f, rock: 0f, sand: 0f, snow: 0f);
        static readonly BiomeSplatBias MountainsBias = new(dirt: 0.15f, rock: 0.30f, sand: 0f, snow: 0f);
        static readonly BiomeSplatBias DesertBias = new(dirt: 0.15f, rock: 0f, sand: 0.80f, snow: 0f);
        static readonly BiomeSplatBias SnowBias = new(dirt: 0f, rock: 0f, sand: 0f, snow: 0.85f);

        /// <summary>The full tilt of one biome. Meadow, and any id outside the enum, has none.</summary>
        public static BiomeSplatBias For(BiomeId biome) => biome switch
        {
            BiomeId.Forest => ForestBias,
            BiomeId.Marsh => MarshBias,
            BiomeId.Mountains => MountainsBias,
            BiomeId.Desert => DesertBias,
            BiomeId.Snow => SnowBias,
            _ => default,
        };

        /// <summary>Each biome's tilt weighted by its share at the point. Continuous in the shares, so a band boundary
        /// fades over the field's blend window. A single biome's weights give exactly <see cref="For"/>.</summary>
        public static BiomeSplatBias Blend(in BiomeWeights biomes)
        {
            float dirt = 0f, rock = 0f, sand = 0f, snow = 0f;
            // A biome added after Snow without a row in For() has no tilt, so stopping at Snow loses nothing.
            for (var biome = BiomeId.Meadow; biome <= BiomeId.Snow; biome++)
            {
                float share = biomes[biome];
                if (share == 0f) continue;
                BiomeSplatBias b = For(biome);
                dirt += share * b.Dirt;
                rock += share * b.Rock;
                sand += share * b.Sand;
                snow += share * b.Snow;
            }
            return new BiomeSplatBias(dirt, rock, sand, snow);
        }
    }
}
