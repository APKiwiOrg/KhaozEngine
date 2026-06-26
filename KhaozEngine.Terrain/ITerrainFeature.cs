namespace KhaozEngine.Terrain
{
    /// <summary>A composable height modifier folded by TerrainField.SampleHeight in list order. Stateless and
    /// pure: Apply must depend only on (x, z, h), so terrain stays load-order independent.</summary>
    public interface ITerrainFeature
    {
        float Apply(float x, float z, float h);
    }
}
