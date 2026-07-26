namespace KhaozEngine.Terrain
{
    /// <summary>Integer index of a square terrain chunk in the streaming grid. (X, Z) maps to the world region
    /// whose -X/-Z corner is (X*chunkSize, Z*chunkSize). Value equality, so it is a dictionary key for the
    /// streamer's loaded set. Aligned with Sharding's CellCoord convention (floor(world / size)); a Sharding cell
    /// is a whole number of these chunks (the 6b ratio).</summary>
    public readonly record struct ChunkCoord(int X, int Z);
}
