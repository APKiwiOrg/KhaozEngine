namespace KhaozEngine.Terrain
{
    /// <summary>A square world-space tile to mesh, in metres. Size defaults to ~60 m so a Sharding CellCoord maps
    /// to a whole number of chunks (exact ratio is a World streaming concern). OriginX/OriginZ is the -X/-Z corner.</summary>
    public readonly struct TerrainChunkRegion
    {
        public const float DefaultSize = 60f;
        public float OriginX { get; init; }
        public float OriginZ { get; init; }
        public float Size { get; init; }
    }
}
