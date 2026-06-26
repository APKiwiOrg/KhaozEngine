namespace KhaozEngine.Terrain
{
    /// <summary>Designed terrain region id, assigned per BiomeBand and surfaced by TerrainField.SampleBiome
    /// for splat material selection and gameplay. Distinct from per-vertex splat weights.</summary>
    public enum BiomeId : byte { Meadow, Forest, Marsh, Mountains, Desert, Snow }
}
